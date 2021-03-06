﻿using System.Collections.Generic;
using Simple1C.Interface;

namespace Simple1C.Impl.Generation.Rendering
{
    internal class ClassModel
    {
        public ClassModel()
        {
            Properties = new List<PropertyModel>();
            NestedClasses = new List<ClassModel>();
        }

        public string Name { get; set; }
        public ConfigurationScope? ConfigurationScope { get; set; }
        public string Synonym { get; set; }
        public string ObjectPresentation { get; set; }
        public List<PropertyModel> Properties { get; private set; }
        public List<ClassModel> NestedClasses { get; private set; }
    }
}