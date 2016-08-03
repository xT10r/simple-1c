﻿using System;
using System.Collections.Generic;
using Simple1C.Impl.Com;

namespace Simple1C.Impl.Queriables
{
    internal class QueryField
    {
        private readonly bool isUniqueIdentifier;

        public QueryField(string sourceName, List<string> pathItems, bool needPresentation, bool needType, Type type)
        {
            Type = type;
            PathItems = pathItems.Count == 0 ? new[] {"Ссылка"} : pathItems.ToArray();
            isUniqueIdentifier = PathItems[PathItems.Length - 1] == EntityHelpers.idPropertyName;
            if (isUniqueIdentifier)
                PathItems[PathItems.Length - 1] = "Ссылка";
<<<<<<< HEAD
            Expression = sourceName + "." + string.Join(".", PathItems);
            Alias = Expression.Replace('.', '_');
=======

            var expressionBuilder = new StringBuilder();
            var aliasBuilder = new StringBuilder();
            if (needPresentation)
                expressionBuilder.Append("ПРЕДСТАВЛЕНИЕ(");
            if (needType)
                expressionBuilder.Append("ТИПЗНАЧЕНИЯ(");
            expressionBuilder.Append(sourceName);
            aliasBuilder.Append(sourceName);
            foreach (var item in PathItems)
            {
                expressionBuilder.Append(".");
                expressionBuilder.Append(item);
                aliasBuilder.Append("_");
                aliasBuilder.Append(item);
            }
            if (isUniqueIdentifier)
                aliasBuilder.Append("_ИД");
>>>>>>> Починили баг с сылками
            if (needType)
            {
                Expression = "ТИПЗНАЧЕНИЯ(" + Expression + ")";
                Alias = Alias + "_ТИПЗНАЧЕНИЯ";
            }
            if (needPresentation)
            {
                Expression = "ПРЕДСТАВЛЕНИЕ(" + Expression + ")";
                Alias = Alias + "_ПРЕДСТАВЛЕНИЕ";
            }
        }

        public object GetValue(object queryResultRow)
        {
            var result = ComHelpers.GetProperty(queryResultRow, Alias);
            if (isUniqueIdentifier)
                result = ComHelpers.Invoke(result, EntityHelpers.idPropertyName);
            return result;
        }

        public string[] PathItems { get; private set; }
        public string Alias { get; private set; }
        public string Expression { get; private set; }
        public Type Type { get; private set; }
    }
}