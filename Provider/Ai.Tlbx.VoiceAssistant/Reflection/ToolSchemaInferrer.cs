using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Reflection
{
    /// <summary>
    /// Infers ToolSchema from C# types using reflection.
    /// Schemas are cached for performance.
    /// Note: For AOT scenarios, types must be preserved via DynamicallyAccessedMembers attributes.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Types passed to InferSchema have DynamicallyAccessedMembers constraint")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Nested types inherit preservation from parent type with DynamicallyAccessedMembers")]
    [UnconditionalSuppressMessage("Trimming", "IL2062", Justification = "Generic type arguments are preserved through parent type annotations")]
    public static class ToolSchemaInferrer
    {
        private const DynamicallyAccessedMemberTypes RequiredMembers =
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.Interfaces;
        private static readonly ConcurrentDictionary<Type, ToolSchema> _cache = new();
        private static readonly NullabilityInfoContext _nullabilityContext = new();

        /// <summary>
        /// Infers a ToolSchema from the given type.
        /// Results are cached.
        /// </summary>
        /// <typeparam name="T">The type to infer schema from.</typeparam>
        /// <returns>The inferred ToolSchema.</returns>
        public static ToolSchema InferSchema<[DynamicallyAccessedMembers(RequiredMembers)] T>() where T : notnull
        {
            return InferSchema(typeof(T));
        }

        /// <summary>
        /// Infers a ToolSchema from the given type.
        /// Results are cached.
        /// </summary>
        /// <param name="type">The type to infer schema from.</param>
        /// <returns>The inferred ToolSchema.</returns>
        public static ToolSchema InferSchema([DynamicallyAccessedMembers(RequiredMembers)] Type type)
        {
            return _cache.GetOrAdd(type, static t => BuildSchema(t));
        }

        private static ToolSchema BuildSchema([DynamicallyAccessedMembers(RequiredMembers)] Type type)
        {
            var schema = new ToolSchema();

            var constructor = type.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (constructor != null && constructor.GetParameters().Length > 0)
            {
                var writableProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite)
                    .ToArray();

                foreach (var constructorParameter in constructor.GetParameters())
                {
                    var parameterName = ToSnakeCase(constructorParameter.Name ?? "unknown");
                    var matchingProperty = FindMatchingProperty(writableProperties, constructorParameter);
                    var toolParam = BuildParameter(constructorParameter.ParameterType, constructorParameter, matchingProperty);

                    schema.Parameters[parameterName] = toolParam;

                    if (IsRequired(constructorParameter, matchingProperty))
                    {
                        schema.Required.Add(parameterName);
                    }
                }
            }
            else
            {
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite);

                foreach (var property in properties)
                {
                    var propertyName = ToSnakeCase(property.Name);
                    var toolParam = BuildParameter(property.PropertyType, null, property);

                    schema.Parameters[propertyName] = toolParam;

                    if (IsRequired(property, null))
                    {
                        schema.Required.Add(propertyName);
                    }
                }
            }

            return schema;
        }

        private static ToolParameter BuildParameter(
            [DynamicallyAccessedMembers(RequiredMembers)] Type type,
            ParameterInfo? parameterInfo,
            PropertyInfo? propertyInfo)
        {
            var param = new ToolParameter
            {
                Description = GetDescription(parameterInfo, propertyInfo)
            };

            var underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
            {
                param.Nullable = true;
                type = underlyingType;
            }

            if (!type.IsValueType &&
                (IsNullableReferenceType(parameterInfo) || IsNullableReferenceType(propertyInfo)))
            {
                param.Nullable = true;
            }

            param.Type = TypeMapper.MapType(type);

            if (type.IsEnum)
            {
                param.Type = ToolParameterType.String;
                param.Enum = Enum.GetNames(type).Select(ToSnakeCase).ToList();
            }

            if (TryGetCollectionElementType(type, out var elementType))
            {
                param.Type = ToolParameterType.Array;
                param.Items = BuildParameter(elementType, null, null);
            }

            if (param.Type == ToolParameterType.Object && !type.IsEnum && type != typeof(object))
            {
                param.Properties = new Dictionary<string, ToolParameter>();
                param.RequiredProperties = new List<string>();

                var nestedProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite);

                foreach (var nestedProp in nestedProps)
                {
                    var propName = ToSnakeCase(nestedProp.Name);
                    param.Properties[propName] = BuildParameter(nestedProp.PropertyType, null, nestedProp);

                    if (IsRequired(nestedProp, null))
                    {
                        param.RequiredProperties.Add(propName);
                    }
                }
            }

            param.Format = TypeMapper.GetFormat(type);
            param.Default = GetDefaultValue(parameterInfo, propertyInfo, type);
            ApplyValidationAttributes(param, parameterInfo, propertyInfo);

            return param;
        }

        private static bool IsRequired(ParameterInfo parameter, PropertyInfo? matchingProperty)
        {
            if (HasDefaultValue(parameter, matchingProperty))
                return false;

            if (HasExplicitRequiredAttribute(parameter, matchingProperty))
                return true;

            if (Nullable.GetUnderlyingType(parameter.ParameterType) != null)
                return false;

            if (IsNullableReferenceType(parameter) || IsNullableReferenceType(matchingProperty))
                return false;

            return true;
        }

        private static bool IsRequired(PropertyInfo property, ParameterInfo? matchingParameter)
        {
            if (HasDefaultValue(matchingParameter, property))
                return false;

            if (HasExplicitRequiredAttribute(matchingParameter, property))
                return true;

            if (Nullable.GetUnderlyingType(property.PropertyType) != null)
                return false;

            if (IsNullableReferenceType(property) || IsNullableReferenceType(matchingParameter))
                return false;

            return true;
        }

        private static bool IsNullableReferenceType(object? memberInfo)
        {
            if (memberInfo == null)
                return false;

            try
            {
                NullabilityInfo? nullabilityInfo = memberInfo switch
                {
                    ParameterInfo pi => _nullabilityContext.Create(pi),
                    PropertyInfo prop => _nullabilityContext.Create(prop),
                    _ => null
                };

                return nullabilityInfo?.WriteState == NullabilityState.Nullable ||
                       nullabilityInfo?.ReadState == NullabilityState.Nullable;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsListType(Type type)
        {
            if (!type.IsGenericType)
                return false;

            var genericDef = type.GetGenericTypeDefinition();
            return genericDef == typeof(List<>) ||
                   genericDef == typeof(IList<>) ||
                   genericDef == typeof(ICollection<>) ||
                   genericDef == typeof(IEnumerable<>) ||
                   genericDef == typeof(IReadOnlyList<>) ||
                   genericDef == typeof(IReadOnlyCollection<>) ||
                   genericDef == typeof(HashSet<>) ||
                   genericDef == typeof(ISet<>);
        }

        private static bool IsDictionaryType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
        {
            if (!type.IsGenericType)
                return false;

            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(Dictionary<,>) ||
                genericDef == typeof(IDictionary<,>) ||
                genericDef == typeof(IReadOnlyDictionary<,>))
            {
                return true;
            }

            return type.GetInterfaces().Any(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }

        private static bool TryGetCollectionElementType(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type,
            out Type elementType)
        {
            if (type == typeof(string) || IsDictionaryType(type))
            {
                elementType = typeof(object);
                return false;
            }

            if (type.IsArray)
            {
                var arrayElementType = type.GetElementType();
                if (arrayElementType != null)
                {
                    elementType = arrayElementType;
                    return true;
                }
            }

            if (type.IsGenericType && IsListType(type))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }

            var enumerableInterface = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            if (enumerableInterface != null)
            {
                var candidate = enumerableInterface.GetGenericArguments()[0];
                if (candidate != typeof(char))
                {
                    elementType = candidate;
                    return true;
                }
            }

            elementType = typeof(object);
            return false;
        }

        private static PropertyInfo? FindMatchingProperty(IEnumerable<PropertyInfo> properties, ParameterInfo parameter)
        {
            var parameterName = parameter.Name ?? string.Empty;
            if (string.IsNullOrEmpty(parameterName))
                return null;

            var exactMatch = properties.FirstOrDefault(p =>
                string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                return exactMatch;
            }

            var normalized = NormalizeName(parameterName);
            return properties.FirstOrDefault(p => NormalizeName(p.Name) == normalized);
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static bool HasDefaultValue(ParameterInfo? parameterInfo, PropertyInfo? propertyInfo)
        {
            if (parameterInfo?.HasDefaultValue == true)
                return true;

            return GetAttribute<DefaultValueAttribute>(parameterInfo, propertyInfo) != null;
        }

        private static bool HasExplicitRequiredAttribute(ParameterInfo? parameterInfo, PropertyInfo? propertyInfo)
        {
            return GetAttribute<RequiredAttribute>(parameterInfo, propertyInfo) != null ||
                   propertyInfo?.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null;
        }

        private static string? GetDescription(ParameterInfo? parameterInfo, PropertyInfo? propertyInfo)
        {
            var parameterDescription = parameterInfo?.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(parameterDescription))
            {
                return parameterDescription;
            }

            return propertyInfo?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        }

        private static TAttribute? GetAttribute<TAttribute>(ParameterInfo? parameterInfo, PropertyInfo? propertyInfo)
            where TAttribute : Attribute
        {
            return parameterInfo?.GetCustomAttribute<TAttribute>() ??
                   propertyInfo?.GetCustomAttribute<TAttribute>();
        }

        private static object? GetDefaultValue(ParameterInfo? parameterInfo, PropertyInfo? propertyInfo, Type type)
        {
            if (parameterInfo is { HasDefaultValue: true } &&
                parameterInfo.DefaultValue != null &&
                parameterInfo.DefaultValue != DBNull.Value)
            {
                return ConvertDefaultValue(parameterInfo.DefaultValue, type);
            }

            var defaultAttribute = GetAttribute<DefaultValueAttribute>(parameterInfo, propertyInfo);
            if (defaultAttribute?.Value != null)
            {
                return ConvertDefaultValue(defaultAttribute.Value, type);
            }

            return null;
        }

        private static void ApplyValidationAttributes(
            ToolParameter parameter,
            ParameterInfo? parameterInfo,
            PropertyInfo? propertyInfo)
        {
            var range = GetAttribute<RangeAttribute>(parameterInfo, propertyInfo);
            if (range != null)
            {
                if (TryConvertToDouble(range.Minimum, out var minimum))
                {
                    parameter.Minimum = minimum;
                }

                if (TryConvertToDouble(range.Maximum, out var maximum))
                {
                    parameter.Maximum = maximum;
                }
            }

            var stringLength = GetAttribute<StringLengthAttribute>(parameterInfo, propertyInfo);
            if (stringLength != null)
            {
                if (stringLength.MaximumLength > 0)
                {
                    parameter.MaxLength = stringLength.MaximumLength;
                }

                if (stringLength.MinimumLength > 0)
                {
                    parameter.MinLength = stringLength.MinimumLength;
                }
            }

            var minLength = GetAttribute<MinLengthAttribute>(parameterInfo, propertyInfo);
            if (minLength != null)
            {
                parameter.MinLength = parameter.MinLength.HasValue
                    ? Math.Max(parameter.MinLength.Value, minLength.Length)
                    : minLength.Length;
            }

            var maxLength = GetAttribute<MaxLengthAttribute>(parameterInfo, propertyInfo);
            if (maxLength != null)
            {
                parameter.MaxLength = parameter.MaxLength.HasValue
                    ? Math.Min(parameter.MaxLength.Value, maxLength.Length)
                    : maxLength.Length;
            }

            var regex = GetAttribute<RegularExpressionAttribute>(parameterInfo, propertyInfo);
            if (!string.IsNullOrWhiteSpace(regex?.Pattern))
            {
                parameter.Pattern = regex.Pattern;
            }
        }

        private static bool TryConvertToDouble(object? value, out double result)
        {
            switch (value)
            {
                case null:
                    result = default;
                    return false;
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = l;
                    return true;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    result = parsed;
                    return true;
                default:
                    try
                    {
                        result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch
                    {
                        result = default;
                        return false;
                    }
            }
        }

        private static object? ConvertDefaultValue(object defaultValue, Type type)
        {
            if (type.IsEnum)
            {
                try
                {
                    var enumValue = defaultValue is string enumString
                        ? Enum.Parse(type, enumString, ignoreCase: true)
                        : Enum.ToObject(type, defaultValue);
                    return ToSnakeCase(enumValue.ToString() ?? string.Empty);
                }
                catch
                {
                    return ToSnakeCase(defaultValue.ToString() ?? string.Empty);
                }
            }

            return defaultValue;
        }

        private static string ToSnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var result = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2");
            return result.ToLowerInvariant();
        }
    }
}
