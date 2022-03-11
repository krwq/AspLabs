using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Microsoft.AspNetCore.Grpc.HttpApi.Internal.Json
{
    internal class JsonTypeResolver
    {
        private JsonSettings _settings;

        public JsonTypeResolver(JsonSettings settings)
        {
            _settings = settings;
        }

        internal void OnContractIntializing(JsonTypeInfo typeInfo)
        {
            if (!typeof(IMessage).IsAssignableFrom(typeInfo.Type))
                return;

            Console.WriteLine($"Initializing type: {typeInfo.Type.FullName}");
            var messageProto = (IMessage)Activator.CreateInstance(typeInfo.Type)!;

            var grpcFields = messageProto.Descriptor.Fields.InFieldNumberOrder();

            List<JsonPropertyInfo> jsonProperties = new();

            foreach (var field in grpcFields)
            {
                Type fieldType = JsonConverterHelper.GetFieldType(field);
                JsonPropertyInfo prop;

                if (field.IsMap)
                {
                    var mapFields = field.MessageType.Fields.InFieldNumberOrder();
                    var mapKey = mapFields[0];
                    var mapValue = mapFields[1];

                    var keyType = JsonConverterHelper.GetFieldType(mapKey);
                    var valueType = JsonConverterHelper.GetFieldType(mapValue);

                    var repeatedFieldType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);

                    prop = typeInfo.CreateJsonProperty(typeof(IDictionary), field.JsonName);
                    prop.Get = (o) => field.Accessor.GetValue((IMessage)o);
                    prop.ReadObject = (object parentObj, ref Utf8JsonReader reader) =>
                    {
                        var newValues = (IDictionary)JsonSerializer.Deserialize(ref reader, repeatedFieldType, prop.Options)!;

                        var existingValue = (IDictionary)field.Accessor.GetValue((IMessage)parentObj);
                        foreach (DictionaryEntry item in newValues)
                        {
                            existingValue[item.Key] = item.Value;
                        }
                    };
                }
                else if (field.IsRepeated)
                {
                    prop = typeInfo.CreateJsonProperty(typeof(IList<>).MakeGenericType(fieldType), field.JsonName);
                    prop.Get = (o) => field.Accessor.GetValue((IMessage)o);
                    prop.ReadObject = (object parentObj, ref Utf8JsonReader reader) =>
                    {
                        JsonConverterHelper.PopulateList(ref reader, prop.Options, (IMessage)parentObj, field);
                    };
                }
                else
                {
                    prop = typeInfo.CreateJsonProperty(fieldType, field.JsonName);
                    prop.Get = (o) => field.Accessor.GetValue((IMessage)o);
                    prop.Set = (o, val) =>
                    {
                        var message = (IMessage)o;
                        ValidateOneOf(field, message);
                        field.Accessor.SetValue(message, val);
                    };
                }

                prop.CanSerialize = (parentObj, value) =>
                {
                    IMessage message = (IMessage)parentObj;
                    return ShouldFormatFieldValue(message, field, value, _settings.FormatDefaultValues);
                };

                jsonProperties.Add(prop);
            }

            typeInfo.Properties = jsonProperties;
        }

        /// <summary>
        /// Determines whether or not a field value should be serialized according to the field,
        /// its value in the message, and the settings of this formatter.
        /// </summary>
        internal static bool ShouldFormatFieldValue(IMessage message, FieldDescriptor field, object? value, bool formatDefaultValues) =>
            field.HasPresence
            // Fields that support presence *just* use that
            ? field.Accessor.HasValue(message)
            // Otherwise, format if either we've been asked to format default values, or if it's
            // not a default value anyway.
            : formatDefaultValues || !IsDefaultValue(field, value);

        private static bool IsDefaultValue(FieldDescriptor descriptor, object? value)
        {
            if (descriptor.IsMap)
            {
                IDictionary dictionary = (IDictionary)value!;
                return dictionary.Count == 0;
            }
            if (descriptor.IsRepeated)
            {
                IList list = (IList)value!;
                return list.Count == 0;
            }
            switch (descriptor.FieldType)
            {
                case FieldType.Bool:
                    return (bool)value! == false;
                case FieldType.Bytes:
                    return (ByteString)value! == ByteString.Empty;
                case FieldType.String:
                    return (string?)value == "";
                case FieldType.Double:
                    return (double?)value == 0.0;
                case FieldType.SInt32:
                case FieldType.Int32:
                case FieldType.SFixed32:
                case FieldType.Enum:
                    return (int)value == 0;
                case FieldType.Fixed32:
                case FieldType.UInt32:
                    return (uint)value == 0;
                case FieldType.Fixed64:
                case FieldType.UInt64:
                    return (ulong)value == 0;
                case FieldType.SFixed64:
                case FieldType.Int64:
                case FieldType.SInt64:
                    return (long)value == 0;
                case FieldType.Float:
                    return (float)value == 0f;
                case FieldType.Message:
                case FieldType.Group: // Never expect to get this, but...
                    return value == null;
                default:
                    throw new ArgumentException("Invalid field type");
            }
        }

        private static void ValidateOneOf(FieldDescriptor fieldDescriptor, IMessage message)
        {
            if (fieldDescriptor.ContainingOneof != null)
            {
                if (fieldDescriptor.ContainingOneof.Accessor.GetCaseFieldDescriptor(message) != null)
                {
                    throw new InvalidOperationException($"Multiple values specified for oneof {fieldDescriptor.ContainingOneof.Name}");
                }
            }
        }

        private static Dictionary<string, FieldDescriptor> CreateJsonFieldMap(IList<FieldDescriptor> fields)
        {
            var map = new Dictionary<string, FieldDescriptor>();
            foreach (var field in fields)
            {
                map[field.Name] = field;
                map[field.JsonName] = field;
            }
            return new Dictionary<string, FieldDescriptor>(map);
        }
    }
}
