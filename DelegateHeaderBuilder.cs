using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DelegateMethodHeaderBuilder;

// Credits:
//  https://stackoverflow.com/a/18891072/21072788
//  ^^ GetEnumFullName
// -------------------------------------------------

public class DelegateHeaderBuilder
{
    private const string DefaultMethodAccessModifier = "private";
    private const string UnknownAttributeTypeArgPlaceholder = "<unknown argument>";

    private static readonly ImmutableArray<string> s_ignoredAttributeNames =
    [
        "System.Reflection.RuntimeCustomAttributeData"
    ];

    public static string BuildMethodHeader(Func<object, object> function, bool normalizeMethodName = true)
    {
        var targetMethod = function.Method;
        var builder = new StringBuilder();

        EmitMethodCustomAttributes(targetMethod, ref builder);
        builder.AppendLine(ConvertMethodHeader(targetMethod));

        string result = builder.ToString();
        if (normalizeMethodName)
        {
            result = result.Replace("$", "")
                .Replace("<", "")
                .Replace(">", "");
        }
        return result;
    }

    private static string ConvertMethodHeader(MethodInfo info)
    {
        string accessModifier = GetAccessModifier(info);
        string? staticOrNull = info.IsStatic ? "static" : null;
        string? abstractOrNull = info.IsAbstract ? "abstract" : null;
        string? virtualOrNull = info.IsVirtual ? "virtual" : null;
        string genericArgs = info.GetGenericArguments().Length > 0
             ? "<" + string.Join(", ", info.GetGenericArguments().Select(arg => arg.Name)) + ">"
             : string.Empty;
        string returnType = info.ReturnType.FullName ?? info.ReturnType.Name;
        var builder = new StringBuilder();
        builder.Append(accessModifier);
        builder.Append(' ');

        if (staticOrNull is not null)
        {
            builder.Append(staticOrNull);
            builder.Append(' ');
        }

        if (abstractOrNull is not null)
        {
            builder.Append(abstractOrNull);
            builder.Append(' ');
        }

        if (virtualOrNull is not null)
        {
            builder.Append(virtualOrNull);
            builder.Append(' ');
        }

        builder.Append(returnType);
        builder.Append(' ');

        builder.Append(info.Name);

        if (genericArgs != string.Empty)
        {
            builder.Append(genericArgs);
        }

        builder.Append('(');
        builder.Append(string.Join(", ", info.GetParameters().Select(ConvertParameter)));
        builder.Append(')');

        return builder.ToString();
    }

    private static string ConvertParameter(ParameterInfo info)
    {
        string name = info.Name!;
        string? defaultValue = info.DefaultValue is not null
            ? ConvertWellKnownBoxedTypeToCSharp(info.DefaultValue)
            : null;
        string typeString = info.ParameterType.FullName ?? info.ParameterType.Name;
        string? @in = info.IsIn ? "in" : null;
        string? @out = info.IsOut ? "out" : null;
        string? args = info.CustomAttributes.Any()
            ? ConvertCustomAttributes(info.CustomAttributes)
            : null;

        if (@in is not null && @out is not null)
        {
            throw new InvalidOperationException("Parameter cannot have both in and out flags");
        }

        var builder = new StringBuilder();

        if (args is not null)
        {
            builder.Append(string.Join(" ", args.Split([Environment.NewLine], StringSplitOptions.None)));
            builder.Append(' ');
        }

        if (@in is not null)
        {
            builder.Append(@in);
            builder.Append(' ');
        }

        if (@out is not null)
        {
            builder.Append(@out);
            builder.Append(' ');
        }

        builder.Append(typeString);
        builder.Append(' ');
        builder.Append(name);

        if (defaultValue is not null)
        {
            if (defaultValue != string.Empty)
            {
                builder.Append(" = ");
                builder.Append(defaultValue);
            }
        }

        return builder.ToString();
    }

    private static void EmitMethodCustomAttributes(MethodInfo method, ref StringBuilder sb)
    {
        foreach (var line in ConvertCustomAttributes(method.CustomAttributes).Split(Environment.NewLine))
        {
            sb.AppendLine(line);
        }
    }

    private static string ConvertCustomAttributes(IEnumerable<CustomAttributeData> datas)
    {
        var sb = new StringBuilder();
        foreach (var attribute in datas)
        {
            string attributeName = attribute.GetType().FullName ?? attribute.GetType().Name;

            if (s_ignoredAttributeNames.Contains(attributeName))
                continue; // Ignore System.Reflection.RuntimeCustomAttributeData, this attribute is literally undocumented

            string namedArgs = string.Join(", ", attribute.NamedArguments.Select(ConvertCustomAttributeNamedArgument));
            string args = string.Join(", ", attribute.ConstructorArguments.Select(ConvertConstructorArgument));
            string combinedArgs =
                namedArgs.Trim().Length == 0
                ? args
                : args + ", " + namedArgs;
            sb.AppendLine($"[{attributeName}({combinedArgs})]");
        }
        return sb.ToString();
    }

    private static string ConvertCustomAttributeNamedArgument(CustomAttributeNamedArgument arg)
    {
        string name = arg.MemberName;
        string result = ConvertWellKnownBoxedTypeToCSharp(arg.TypedValue);
        if (result != "")
        {
            return $"{name} = {result}";
        }
        return name;
    }

    private static string ConvertConstructorArgument(CustomAttributeTypedArgument arg)
    {
        return ConvertWellKnownBoxedTypeToCSharp(arg.Value);
    }

    private static string ConvertWellKnownBoxedTypeToCSharp(object? arg)
    {
        if (arg is null)
        {
            return "null";
        }

        Type? argType = arg?.GetType();
        if (argType == typeof(string))
        {
            string contents = arg!.ToString()!.Replace("\"", "\\\"").Replace("\\", "\\\\");
            return $"\"{contents}\"";
        }
        else if (argType == typeof(bool))
        {
            bool result = (bool)arg!;
            return result.ToString().ToLowerInvariant();
        }
        else if (argType == typeof(byte) || argType == typeof(sbyte) ||
            argType == typeof(short) || argType == typeof(ushort) ||
            argType == typeof(int) || argType == typeof(uint) ||
            argType == typeof(long) || argType == typeof(ulong))
        {
            // Integer types contain the raw integer value ...
            return arg?.ToString() ?? "0";
        }
        else if (argType == typeof(float) || argType == typeof(double) || argType == typeof(decimal))
        {
            // We need to stringify the result in such a way so that the decimal
            // point is always the period (.) instead of a comma (,). Sometimes, if
            // you change a language of the system or the culture of the application
            // to something like German or Russian where the preferred decimal
            // point is a comma, then .ToString() will use the comma as a decimal point.
            // C# only accepts the period as the decimal point notation, so I'll use
            // CultureInfo.InvariantCulture so that the stringified decimal point is always
            // the period, independent of the system language.
            if (arg is float floatType)
            {
                return floatType.ToString(CultureInfo.InvariantCulture);
            }
            else if (arg is double doubleType)
            {
                return doubleType.ToString(CultureInfo.InvariantCulture);
            }
            else if (arg is decimal decimalType)
            {
                return decimalType.ToString(CultureInfo.InvariantCulture);
            }
            throw new InvalidOperationException("Unreachable floating-point type");
        }
        else if (argType == typeof(char))
        {
            return $"'{arg}'";
        }
        else if (argType == typeof(Enum))
        {
            var enumType = (Enum)arg!;
            return GetEnumFullName(enumType);
        }
        else if (argType == typeof(Type))
        {
            var type = (Type)arg!;
            var fullName = type.FullName ?? type.Name;
            return $"typeof({fullName})";
        }
        else if (argType == typeof(DBNull))
        {
            return "";
        }
        return UnknownAttributeTypeArgPlaceholder + $" (of type {argType?.Name ?? "null"})";
    }

    private static string GetAccessModifier(MethodInfo info)
    {
        // IL access modifiers include:
        //   public - public
        //   private - private
        //   family - protected
        //   assembly - internal
        //   famorassem - protected internal
        //   famandassem - private protected

        if (info.IsPublic)
        {
            return "public";
        }
        else if (info.IsPrivate)
        {
            return "private";
        }
        else if (info.IsFamily)
        {
            return "protected";
        }
        else if (info.IsAssembly)
        {
            return "internal";
        }
        else if (info.IsFamilyOrAssembly)
        {
            return "protected internal";
        }
        else if (info.IsFamilyAndAssembly)
        {
            return "private protected";
        }
        else
        {
            return DefaultMethodAccessModifier;
        }
    }

    private static string GetEnumFullName(Enum @enum)
    {
        return string.Format("{0}.{1}", @enum.GetType().Name, @enum.ToString());
    }
}
