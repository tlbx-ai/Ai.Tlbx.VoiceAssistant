using System;

namespace Ai.Tlbx.VoiceAssistant.Attributes
{
    /// <summary>
    /// Advertises a fixed set of allowed string values in the generated tool schema.
    /// Use this when the runtime argument should stay a string but the model should see a JSON Schema enum.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ToolEnumValuesAttribute : Attribute
    {
        public ToolEnumValuesAttribute(params string[] values)
        {
            Values = values;
        }

        public string[] Values { get; }
    }
}
