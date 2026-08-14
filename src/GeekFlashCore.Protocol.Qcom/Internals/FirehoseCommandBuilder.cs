using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal static class FirehoseCommandBuilder
{
    private const int InitialCapacity = 256;

    private static readonly ConcurrentDictionary<Type, CommandSchema> Schemas = new();

    public static string Build<T>(this T command) where T : BaseCommand
    {
        ArgumentNullException.ThrowIfNull(command);
        CommandSchema schema = Schemas.GetOrAdd(command.GetType(), static type => CommandSchema.Create(type));
        return schema.Build(command);
    }

    private enum ValueKind
    {
        String,
        Enum,
        Number
    }

    private sealed class CommandSchema
    {
        private readonly string _tag;
        private readonly PropertySerializer[] _serializers;

        private CommandSchema(string tag, PropertySerializer[] serializers)
        {
            _tag = tag;
            _serializers = serializers;
        }

        public static CommandSchema Create(Type type)
        {
            string? tag = type.GetCustomAttribute<FirehoseCmdTagAttribute>()?.Tag;
            if (string.IsNullOrEmpty(tag))
                throw new InvalidOperationException(Strings.FormatFirehose_CommandTagMissing(type.Name));

            PropertySerializer[] serializers = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.GetCustomAttribute<FirehoseCmdAttributeAttribute>() is not null)
                .OrderBy(static property => property.MetadataToken)
                .Select(static property =>
                {
                    string attributeName = property.GetCustomAttribute<FirehoseCmdAttributeAttribute>()!.AttributeName;
                    return new PropertySerializer(attributeName, GetValueKind(property.PropertyType), CreateGetter(property));
                })
                .ToArray();

            return new CommandSchema(tag, serializers);
        }

        public string Build(object command)
        {
            var builder = new StringBuilder(InitialCapacity);
            builder.Append(FirehoseConstants.XmlDeclaration);
            builder.Append('<').Append(_tag);
            foreach (PropertySerializer serializer in _serializers)
            {
                object? value = serializer.Getter(command);
                if (value is null)
                    continue;
                builder.Append(' ').Append(serializer.AttributeName).Append("=\"");
                serializer.AppendValue(builder, value);
                builder.Append('"');
            }
            builder.Append(" />").Append(FirehoseConstants.XmlDataEnd);
            return builder.ToString();
        }

        private static ValueKind GetValueKind(Type propertyType)
        {
            Type type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (type == typeof(string))
                return ValueKind.String;
            if (type.IsEnum)
                return ValueKind.Enum;
            if (IsSupportedNumber(type))
                return ValueKind.Number;
            throw new InvalidOperationException(
                Strings.FormatFirehose_UnsupportedPropertyType(propertyType.Name));
        }

        private static bool IsSupportedNumber(Type type) => Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => true,
            _ => false
        };

        private static Func<object, object?> CreateGetter(PropertyInfo property)
        {
            ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
            UnaryExpression body = Expression.Convert(
                Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
                typeof(object));
            return Expression.Lambda<Func<object, object?>>(body, instance).Compile();
        }
    }

    private sealed class PropertySerializer
    {
        public string AttributeName { get; }
        public Func<object, object?> Getter { get; }

        private readonly ValueKind _kind;

        public PropertySerializer(string attributeName, ValueKind kind, Func<object, object?> getter)
        {
            AttributeName = attributeName;
            _kind = kind;
            Getter = getter;
        }

        public void AppendValue(StringBuilder builder, object value)
        {
            switch (_kind)
            {
                case ValueKind.String:
                    AppendXmlEscaped(builder, (string)value);
                    break;
                case ValueKind.Enum:
                    builder.Append(value switch
                    {
                        FirehoseStorage storage => storage.ToWireString(),
                        FirehosePowerValue power => power.ToWireString(),
                        _ => value.ToString() ?? string.Empty
                    });
                    break;
                default:
                    builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static void AppendXmlEscaped(StringBuilder builder, string value)
        {
            int segmentStart = 0;
            for (int i = 0; i < value.Length; i++)
            {
                string? entity = value[i] switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    '"' => "&quot;",
                    '\'' => "&apos;",
                    _ => null
                };

                if (entity is null)
                    continue;

                builder.Append(value, segmentStart, i - segmentStart);
                builder.Append(entity);
                segmentStart = i + 1;
            }

            builder.Append(value, segmentStart, value.Length - segmentStart);
        }
    }
}
