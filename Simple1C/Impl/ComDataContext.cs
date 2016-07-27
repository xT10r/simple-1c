﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Simple1C.Impl.Com;
using Simple1C.Impl.Helpers;
using Simple1C.Impl.Helpers.MemberAccessor;
using Simple1C.Impl.Queriables;
using Simple1C.Interface;
using Simple1C.Interface.ObjectModel;

namespace Simple1C.Impl
{
    internal class ComDataContext : IDataContext
    {
        private readonly GlobalContext globalContext;
        private readonly ComObjectMapper comObjectMapper;
        private readonly IQueryProvider queryProvider;
        private readonly TypeRegistry typeRegistry;
        private readonly MetadataAccessor metadataAccessor;
        private readonly ProjectionMapperFactory projectionMapperFactory;
        private readonly ParametersConverter parametersConverter;
        private static readonly ConcurrentDictionary<Type, bool> hasGroups = new ConcurrentDictionary<Type, bool>();

        public ComDataContext(object globalContext, Assembly mappingsAssembly)
        {
            this.globalContext = new GlobalContext(globalContext);
            typeRegistry = new TypeRegistry(mappingsAssembly);
            comObjectMapper = new ComObjectMapper(typeRegistry, this.globalContext);
            queryProvider = RelinqHelpers.CreateQueryProvider(typeRegistry, Execute);
            metadataAccessor = new MetadataAccessor(this.globalContext);
            projectionMapperFactory = new ProjectionMapperFactory(comObjectMapper);
            parametersConverter = new ParametersConverter(comObjectMapper, this.globalContext);
        }

        public Type GetTypeOrNull(string configurationName)
        {
            return typeRegistry.GetTypeOrNull(configurationName);
        }

        public IQueryable<T> Select<T>(string sourceName = null)
        {
            return new RelinqQueryable<T>(queryProvider, sourceName);
        }

        public void Save(object entity)
        {
            if (entity == null)
                throw new InvalidOperationException("entity is null");
            var constantEntity = entity as Constant;
            if (constantEntity != null)
            {
                SaveConstant(constantEntity);
                return;
            }
            var entitiesToSave = new List<Abstract1CEntity>();
            var abstract1CEntity = entity as Abstract1CEntity;
            if (abstract1CEntity != null)
            {
                abstract1CEntity.Controller.PrepareToSave(abstract1CEntity, entitiesToSave);
                SaveEntities(entitiesToSave);
                return;
            }
            var enumerable = entity as IEnumerable;
            if (enumerable != null)
            {
                foreach (var item in enumerable)
                {
                    if (item == null)
                        throw new InvalidOperationException("some items are null");
                    abstract1CEntity = item as Abstract1CEntity;
                    if (abstract1CEntity != null)
                    {
                        abstract1CEntity.Controller.PrepareToSave(abstract1CEntity, entitiesToSave);
                        continue;
                    }
                    constantEntity = item as Constant;
                    if (constantEntity != null)
                    {
                        SaveConstant(constantEntity);
                        continue;
                    }
                    throw new InvalidOperationException(FormatInvalidEntityTypeMessage(item));
                }
                SaveEntities(entitiesToSave);
                return;
            }
            throw new InvalidOperationException(FormatInvalidEntityTypeMessage(entity));
        }

        private void SaveConstant(Constant constantEntity)
        {
            var constants = ComHelpers.GetProperty(globalContext.ComObject(), "Константы");
            var constant = ComHelpers.GetProperty(constants, constantEntity.GetType().Name);
            var value = comObjectMapper.MapTo1C(constantEntity.ЗначениеНетипизированное);
            ComHelpers.Invoke(constant, "Установить", value);
        }

        private void SaveEntities(List<Abstract1CEntity> entitiesToSave)
        {
            if (entitiesToSave.Count == 0)
                return;
            var pending = new Stack<object>();
            foreach (var e in entitiesToSave)
                Save(e, null, pending);
        }

        private static string FormatInvalidEntityTypeMessage(object item)
        {
            const string messageFormat = "invalid entity type [{0}]";
            return string.Format(messageFormat, item.GetType().FormatName());
        }

        private void Save(Abstract1CEntity source, object comObject, Stack<object> pending)
        {
            if (!source.Controller.IsDirty)
                return;
            if (pending.Contains(source))
            {
                const string messageFormat = "cycle detected for entity type [{0}]: [{1}]";
                throw new InvalidOperationException(string.Format(messageFormat, source.GetType().FormatName(),
                    pending
                        .Reverse()
                        .Select(x => x is Abstract1CEntity ? x.GetType().FormatName() : x)
                        .JoinStrings("->")));
            }
            pending.Push(source);
            ConfigurationName? configurationName;
            if (comObject == null)
            {
                configurationName = ConfigurationName.Get(source.GetType());
                if (configurationName.Value.Scope == ConfigurationScope.РегистрыСведений)
                    comObject = source.Controller.ValueSource == null || !source.Controller.ValueSource.Writable
                        ? CreateRegisterRecordManager(configurationName.Value)
                        : source.Controller.ValueSource.GetBackingStorage();
                else
                    comObject = source.Controller.IsNew
                        ? CreateNewObject(source, configurationName.Value)
                        : ComHelpers.Invoke(source.Controller.ValueSource.GetBackingStorage(), "ПолучитьОбъект");
            }
            else
                configurationName = null;
            bool? newPostingValue = null;
            var changeLog = source.Controller.Changed;
            if (changeLog != null)
                foreach (var p in changeLog)
                {
                    var value = p.Value;
                    if (p.Key == "Проведен" && configurationName.HasValue &&
                        configurationName.Value.Scope == ConfigurationScope.Документы)
                    {
                        newPostingValue = (bool?) value;
                        continue;
                    }
                    pending.Push(p.Key);
                    SaveProperty(p.Key, p.Value, comObject, pending);
                    pending.Pop();
                }
            var needPatchWithOriginalValues = configurationName.HasValue &&
                                              configurationName.Value.Scope == ConfigurationScope.РегистрыСведений &&
                                              source.Controller.ValueSource != null &&
                                              !source.Controller.ValueSource.Writable &&
                                              changeLog != null;
            if (needPatchWithOriginalValues)
            {
                var requisiteNames = metadataAccessor.GetRequisiteNames(configurationName.Value);
                var backingStorage = source.Controller.ValueSource.GetBackingStorage();
                foreach (var requisiteName in requisiteNames)
                    if (!changeLog.ContainsKey(requisiteName))
                    {
                        pending.Push(requisiteName);
                        var value = ComHelpers.GetProperty(backingStorage, requisiteName);
                        SaveProperty(requisiteName, value, comObject, pending);
                        pending.Pop();
                    }
            }
            object valueSourceComObject;
            if (configurationName.HasValue)
            {
                if (!newPostingValue.HasValue && configurationName.Value.Scope == ConfigurationScope.Документы)
                {
                    var oldPostingValue = Convert.ToBoolean(ComHelpers.GetProperty(comObject, "Проведен"));
                    if (oldPostingValue)
                    {
                        Write(comObject, configurationName.Value, false);
                        newPostingValue = true;
                    }
                }
                Write(comObject, configurationName.Value, newPostingValue);
                switch (configurationName.Value.Scope)
                {
                    case ConfigurationScope.Справочники:
                        UpdateIfExists(source, comObject, "Код");
                        break;
                    case ConfigurationScope.Документы:
                        UpdateIfExists(source, comObject, "Номер");
                        break;
                }
                if (configurationName.Value.HasReference)
                {
                    valueSourceComObject = ComHelpers.GetProperty(comObject, "Ссылка");
                    UpdateId(valueSourceComObject, source, configurationName.Value);
                }
                else
                    valueSourceComObject = comObject;
            }
            else
            {
                UpdateIfExists(source, comObject, "НомерСтроки");
                valueSourceComObject = comObject;
            }
            var valueSourceIsWriteable = configurationName.HasValue &&
                                         configurationName.Value.Scope == ConfigurationScope.РегистрыСведений;
            source.Controller.ResetDirty(new ComValueSource(valueSourceComObject, comObjectMapper,
                valueSourceIsWriteable));
            pending.Pop();
        }

        private void SaveProperty(string name, object value, object comObject, Stack<object> pending)
        {
            var list = value as IList;
            if (list != null)
            {
                var tableSection = ComHelpers.GetProperty(comObject, name);
                ComHelpers.Invoke(tableSection, "Очистить");
                foreach (Abstract1CEntity item in (IList) value)
                    Save(item, ComHelpers.Invoke(tableSection, "Добавить"), pending);
                return;
            }
            var syncList = value as SyncList;
            if (syncList != null)
            {
                var tableSection = ComHelpers.GetProperty(comObject, name);
                foreach (var cmd in syncList.Commands)
                    switch (cmd.CommandType)
                    {
                        case SyncList.CommandType.Delete:
                            var deleteCommand = (SyncList.DeleteCommand) cmd;
                            ComHelpers.Invoke(tableSection, "Удалить", deleteCommand.index);
                            break;
                        case SyncList.CommandType.Insert:
                            var insertCommand = (SyncList.InsertCommand) cmd;
                            var newItemComObject = ComHelpers.Invoke(tableSection, "Вставить", insertCommand.index);
                            pending.Push(insertCommand.index);
                            Save(insertCommand.item, newItemComObject, pending);
                            pending.Pop();
                            break;
                        case SyncList.CommandType.Move:
                            var moveCommand = (SyncList.MoveCommand) cmd;
                            ComHelpers.Invoke(tableSection, "Сдвинуть", moveCommand.from, moveCommand.delta);
                            break;
                        case SyncList.CommandType.Update:
                            var updateCommand = (SyncList.UpdateCommand) cmd;
                            pending.Push(updateCommand.index);
                            Save(updateCommand.item, Call.Получить(tableSection, updateCommand.index), pending);
                            pending.Pop();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                return;
            }
            object valueToSet;
            var abstractEntity = value as Abstract1CEntity;
            if (abstractEntity != null)
            {
                Save(abstractEntity, null, pending);
                valueToSet = abstractEntity.Controller.ValueSource.GetBackingStorage();
            }
            else
                valueToSet = comObjectMapper.MapTo1C(value);
            ComHelpers.SetProperty(comObject, name, valueToSet);
        }

        private void Write(object comObject, ConfigurationName name, bool? posting)
        {
            object argument;
            string argumentString;
            if (name.Scope == ConfigurationScope.РегистрыСведений)
            {
                argument = true;
                argumentString = argument.ToString();
            }
            else
            {
                var writeModeName = posting.HasValue
                    ? (posting.Value ? "Проведение" : "ОтменаПроведения")
                    : "Запись";
                var writeMode = globalContext.РежимЗаписиДокумента();
                argument = ComHelpers.GetProperty(writeMode, writeModeName);
                argumentString = "РежимЗаписиДокумента." + writeModeName;
            }
            try
            {
                ComHelpers.Invoke(comObject, "Write", argument);
            }
            catch (TargetInvocationException e)
            {
                const string messageFormat = "error writing document [{0}] with argument [{1}]";
                throw new InvalidOperationException(string.Format(messageFormat,
                    name.Fullname, argumentString), e.InnerException);
            }
        }

        private void UpdateId(object source, Abstract1CEntity target, ConfigurationName name)
        {
            var property = target.GetType().GetProperty(EntityHelpers.idPropertyName);
            if (property == null)
            {
                const string messageFormat = "type [{0}] has no id";
                throw new InvalidOperationException(string.Format(messageFormat, name));
            }
            var idValue = ComHelpers.Invoke(source, EntityHelpers.idPropertyName);
            idValue = comObjectMapper.MapFrom1C(idValue, typeof(Guid));
            SetValueWithoutTracking(target, property, idValue);
        }

        private static void UpdateIfExists(Abstract1CEntity target, object source, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName);
            if (property == null)
                return;
            SetValueWithoutTracking(target, property, ComHelpers.GetProperty(source, propertyName));
        }

        private static void SetValueWithoutTracking(Abstract1CEntity target, PropertyInfo property, object value)
        {
            var oldTrackChanges = target.Controller.TrackChanges;
            target.Controller.TrackChanges = false;
            try
            {
                property.SetValue(target, value);
            }
            finally
            {
                target.Controller.TrackChanges = oldTrackChanges;
            }
        }

        private object CreateRegisterRecordManager(ConfigurationName name)
        {
            return ComHelpers.Invoke(globalContext.GetManager(name), "СоздатьМенеджерЗаписи");
        }

        private object CreateNewObject(Abstract1CEntity entity, ConfigurationName configurationName)
        {
            var manager = globalContext.GetManager(configurationName);
            switch (configurationName.Scope)
            {
                case ConfigurationScope.Справочники:
                    bool hasGroupProperty;
                    var entityType = entity.GetType();
                    if (!hasGroups.TryGetValue(entityType, out hasGroupProperty))
                    {
                        var propertyInfo = entityType.GetProperty("ЭтоГруппа");
                        hasGroupProperty = propertyInfo != null && propertyInfo.PropertyType == typeof(bool);
                        hasGroups.TryAdd(entityType, hasGroupProperty);
                    }
                    object isGroup;
                    return hasGroupProperty
                           && entity.Controller.Changed.TryGetValue("ЭтоГруппа", out isGroup)
                           && (bool) isGroup
                        ? ComHelpers.Invoke(manager, "CreateFolder")
                        : ComHelpers.Invoke(manager, "CreateItem");
                case ConfigurationScope.Документы:
                    return ComHelpers.Invoke(manager, "CreateDocument");
                default:
                    const string messageFormat = "unexpected entityType [{0}]";
                    throw new InvalidOperationException(string.Format(messageFormat, configurationName.Name));
            }
        }

        private IEnumerable Execute(BuiltQuery builtQuery)
        {
            if (EntityHelpers.IsConstant(builtQuery.EntityType))
            {
                var constantWrapType = builtQuery.EntityType.BaseType;
                if (constantWrapType == null)
                    throw new InvalidOperationException("assertion failure");
                var constantWrap = (Constant) FormatterServices.GetUninitializedObject(builtQuery.EntityType);
                var constants = ComHelpers.GetProperty(globalContext.ComObject(), "Константы");
                var constant = ComHelpers.GetProperty(constants, builtQuery.EntityType.Name);
                var value = ComHelpers.Invoke(constant, "Получить");
                constantWrap.ЗначениеНетипизированное = comObjectMapper.MapFrom1C(value,
                    constantWrapType.GetGenericArguments()[0]);
                yield return constantWrap;
                yield break;
            }
            var queryText = builtQuery.QueryText;
            var parameters = builtQuery.Parameters;
            parametersConverter.ConvertParametersTo1C(parameters);
            var hasReference = ConfigurationName.Get(builtQuery.EntityType).HasReference;
            var queryResult = globalContext.Execute(queryText, parameters);
            var selection = queryResult.Select();
            var projection = builtQuery.Projection == null
                ? null
                : projectionMapperFactory.GetMapper(builtQuery.Projection);
            while (selection.Next())
                if (projection != null)
                    yield return projection(selection.ComObject);
                else
                {
                    var sourceObject = hasReference ? selection["Ссылка"] : selection.ComObject;
                    yield return comObjectMapper.MapFrom1C(sourceObject, builtQuery.EntityType);
                }
        }
    }
}