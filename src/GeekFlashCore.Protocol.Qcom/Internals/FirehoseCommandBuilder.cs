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
        private readonly Action<StringBuilder, object>[] _appenders;

        private CommandSchema(string tag, Action<StringBuilder, object>[] appenders)
        {
            _tag = tag;
            _appenders = appenders;
        }

        public static CommandSchema Create(Type type)
        {
            string? tag = type.GetCustomAttribute<FirehoseCmdTagAttribute>()?.Tag;
            if (string.IsNullOrEmpty(tag))
                throw new InvalidOperationException(Strings.FormatFirehose_CommandTagMissing(type.Name));

            Action<StringBuilder, object>[] appenders = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.GetCustomAttribute<FirehoseCmdAttributeAttribute>() is not null)
                .OrderBy(static property => property.MetadataToken)
                .Select(static property =>
                {
                    string attributeName = property.GetCustomAttribute<FirehoseCmdAttributeAttribute>()!.AttributeName;
                    return CreateAppender(property, attributeName, GetValueKind(property.PropertyType));
                })
                .ToArray();

            return new CommandSchema(tag, appenders);
        }

        public string Build(object command)
        {
            var builder = new StringBuilder(InitialCapacity);
            builder.Append(FirehoseConstants.XmlDeclaration);
            builder.Append('<').Append(_tag);
            foreach (Action<StringBuilder, object> append in _appenders)
                append(builder, command);
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

        private static Action<StringBuilder, object> CreateAppender(
            PropertyInfo property,
            string attributeName,
            ValueKind kind)
        {
            ParameterExpression builder = Expression.Parameter(typeof(StringBuilder), "builder");
            ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
            MemberExpression value = Expression.Property(
                Expression.Convert(instance, property.DeclaringType!),
                property);
            Type? nullableType = Nullable.GetUnderlyingType(property.PropertyType);
            Type valueType = nullableType ?? property.PropertyType;
            MethodInfo appendMethod = GetAppendMethod(valueType, kind);
            Expression callValue = nullableType is null ? value : Expression.Property(value, "Value");
            MethodCallExpression append = Expression.Call(
                appendMethod,
                builder,
                Expression.Constant(attributeName),
                callValue);

            Expression body;
            if (nullableType is not null)
            {
                body = Expression.IfThen(Expression.Property(value, "HasValue"), append);
            }
            else if (valueType == typeof(string))
            {
                body = Expression.IfThen(
                    Expression.NotEqual(value, Expression.Constant(null, typeof(string))),
                    append);
            }
            else
            {
                body = append;
            }

            return Expression.Lambda<Action<StringBuilder, object>>(body, builder, instance).Compile();
        }

        private static MethodInfo GetAppendMethod(Type type, ValueKind kind)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            if (type == typeof(string))
                return typeof(CommandSchema).GetMethod(nameof(AppendString), flags)!;
            if (type == typeof(FirehoseStorage))
                return typeof(CommandSchema).GetMethod(nameof(AppendStorage), flags)!;
            if (type == typeof(FirehosePowerValue))
                return typeof(CommandSchema).GetMethod(nameof(AppendPower), flags)!;

            string methodName = kind == ValueKind.Enum ? nameof(AppendEnum) : nameof(AppendNumber);
            return typeof(CommandSchema).GetMethod(methodName, flags)!.MakeGenericMethod(type);
        }

        private static void AppendString(StringBuilder builder, string name, string value)
        {
            AppendPrefix(builder, name);
            AppendXmlEscaped(builder, value);
            builder.Append('"');
        }

        private static void AppendStorage(StringBuilder builder, string name, FirehoseStorage value)
        {
            AppendPrefix(builder, name);
            builder.Append(value.ToWireString()).Append('"');
        }

        private static void AppendPower(StringBuilder builder, string name, FirehosePowerValue value)
        {
            AppendPrefix(builder, name);
            builder.Append(value.ToWireString()).Append('"');
        }

        private static void AppendEnum<T>(StringBuilder builder, string name, T value)
            where T : struct, Enum
        {
            AppendPrefix(builder, name);
            builder.Append(value.ToString()).Append('"');
        }

        private static void AppendNumber<T>(StringBuilder builder, string name, T value)
            where T : struct, ISpanFormattable
        {
            AppendPrefix(builder, name);
            Span<char> buffer = stackalloc char[64];
            if (!value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture))
                throw new InvalidOperationException(Strings.FormatQcom_FirehoseAttributeFormatFailed(name));
            builder.Append(buffer[..written]).Append('"');
        }

        private static void AppendPrefix(StringBuilder builder, string name) =>
            builder.Append(' ').Append(name).Append("=\"");

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
