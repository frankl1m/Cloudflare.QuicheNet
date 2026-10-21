using System;

namespace GroupedNativeMethodsGenerator
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class GroupedNativeMethodsAttribute : Attribute
    {
        public string RemovePrefix { get; }
        public string RemoveSuffix { get; }
        public bool RemoveUntilTypeName { get; }
        public bool FixMethodName { get; }

        public GroupedNativeMethodsAttribute(string removePrefix = "", string removeSuffix = "", bool removeUntilTypeName = true, bool fixMethodName = true)
        {
            this.RemovePrefix = removePrefix;
            this.RemoveSuffix = removeSuffix;
            this.RemoveUntilTypeName = removeUntilTypeName;
            this.FixMethodName = fixMethodName;
        }
    }
}