using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Microsoft.AspNetCore.Grpc.HttpApi.Internal.Json
{
    internal class JsonTypeResolver : DefaultJsonTypeInfoResolver
    {
        private JsonSettings _settings;

        public JsonTypeResolver(JsonSettings settings, JsonSerializerOptions options) : base(options)
        {
            _settings = settings;
        }

        public override JsonTypeInfo GetTypeInfo(Type type)
        {
            if (!typeof(IMessage).IsAssignableFrom(type))
                return base.GetTypeInfo(type);

            var typeInfo = JsonTypeInfo.CreateJsonTypeInfo(type, Options);
            var messageProto = (IMessage)Activator.CreateInstance(typeInfo.Type)!;

            var grpcFields = messageProto.Descriptor.Fields.InFieldNumberOrder();

            BindingFlags bindingFlags = BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var field in grpcFields)
            {
                Type fieldType = JsonConverterHelper.GetFieldType(field);
                JsonPropertyInfo prop;

                if (field.IsMap)
                {
                    var mapFields = field.MessageType.Fields.InFieldNumberOrder();
                    var keyType = JsonConverterHelper.GetFieldType(mapFields[0]);
                    var valueType = JsonConverterHelper.GetFieldType(mapFields[1]);

                    prop = (JsonPropertyInfo)typeof(JsonTypeResolver).GetMethod(nameof(CreateMapProperty), bindingFlags)!
                        .MakeGenericMethod(keyType, valueType)
                        .Invoke(this, new object[] { typeInfo, field })!;
                }
                else if (field.IsRepeated)
                {
                    prop = (JsonPropertyInfo)typeof(JsonTypeResolver).GetMethod(nameof(CreateRepeatedProperty), bindingFlags)!
                        .MakeGenericMethod(fieldType)
                        .Invoke(this, new object[] { typeInfo, field })!;
                }
                else
                {
                    prop = (JsonPropertyInfo)typeof(JsonTypeResolver).GetMethod(nameof(CreateProperty), bindingFlags)!
                        .MakeGenericMethod(fieldType)
                        .Invoke(this, new object[] { typeInfo, field })!;
                }

                typeInfo.Properties.Add(prop);
            }

            return typeInfo;
        }

        private JsonPropertyInfo<IDictionary<TKey, TValue>> CreateMapProperty<TKey, TValue>(JsonTypeInfo typeInfo, FieldDescriptor field)
        {
            JsonPropertyInfo<IDictionary<TKey, TValue>> prop = typeInfo.CreateJsonProperty<IDictionary<TKey, TValue>>(field.JsonName);
            prop.Get = (o) => (IDictionary<TKey, TValue>)field.Accessor.GetValue((IMessage)o);
            prop.Set = (o, val) =>
            {
                IDictionary<TKey, TValue> source = val!;
                IDictionary<TKey, TValue> destination = (IDictionary<TKey, TValue>)field.Accessor.GetValue((IMessage)o);
                foreach (var el in source)
                {
                    destination.Add(el.Key, el.Value);
                }
            };
            prop.CanSerialize = CanSerializeMethod<IDictionary<TKey, TValue>>(field);

            return prop;
        }

        private JsonPropertyInfo<IList<T>> CreateRepeatedProperty<T>(JsonTypeInfo typeInfo, FieldDescriptor field)
        {
            JsonPropertyInfo<IList<T>> prop = typeInfo.CreateJsonProperty<IList<T>>(field.JsonName);
            prop.Get = (o) => (IList<T>)field.Accessor.GetValue((IMessage)o);
            prop.Set = (o, val) =>
            {
                IList<T> source = val!;
                IList<T> destination = (IList<T>)field.Accessor.GetValue((IMessage)o);
                foreach (var el in source)
                {
                    destination.Add(el);
                }
            };

            prop.CanSerialize = CanSerializeMethod<IList<T>>(field);

            return prop;
        }

        private JsonPropertyInfo<T> CreateProperty<T>(JsonTypeInfo typeInfo, FieldDescriptor field)
        {
            JsonPropertyInfo<T> prop = typeInfo.CreateJsonProperty<T>(field.JsonName);
            prop.Get = (o) => (T)field.Accessor.GetValue((IMessage)o);
            prop.Set = (o, val) =>
            {
                var message = (IMessage)o;
                ValidateOneOf(field, message);
                field.Accessor.SetValue(message, val);
            };

            prop.CanSerialize = CanSerializeMethod<T>(field);
            return prop;
        }

        private Func<object, T?, bool> CanSerializeMethod<T>(FieldDescriptor field)
        {
            return (object parentObj, T? value) =>
            {
                IMessage message = (IMessage)parentObj;
                return ShouldFormatFieldValue(message, field, value, _settings.FormatDefaultValues);
            };
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
