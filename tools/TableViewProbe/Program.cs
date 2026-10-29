using System;
using System.Linq;
using System.Reflection;

namespace TableViewProbe;

/// <summary>
/// ПЕРВЫЙ ШАГ ПРОБЫ: ЧТО ВООБЩЕ ЕСТЬ В ЯДРЕ. Печатает открытую поверхность типов
/// <c>Avalonia.Controls.TableView*</c> из сборки 12.1.3 — по факту, а не по документации.
/// </summary>
internal static class ApiDump
{
    private static readonly string[] Names =
    {
        "TableView",
        "TableViewColumn",
        "TableViewRow",
        "TableViewCell",
        "TableViewColumnHeader",
        "TableViewSelectionMode",
    };

    public static void Run()
    {
        var assembly = typeof(Avalonia.Controls.Control).Assembly;

        Console.WriteLine($"СБОРКА {assembly.GetName().Name} {assembly.GetName().Version}");

        Console.WriteLine("=== Dispatcher: методы про цикл");
        foreach (var method in typeof(Avalonia.Threading.Dispatcher).GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (!method.Name.Contains("Loop", StringComparison.Ordinal)
                && !method.Name.Contains("RunJobs", StringComparison.Ordinal)) continue;

            Console.WriteLine(
                $"  {(method.IsStatic ? "static " : string.Empty)}{Short(method.ReturnType)} {method.Name}"
                + $"({string.Join(", ", method.GetParameters().Select(P))})");
        }

        foreach (var name in Names)
        {
            var type = assembly.GetType("Avalonia.Controls." + name, throwOnError: false);

            if (type is null)
            {
                Console.WriteLine($"=== {name}: ТИПА НЕТ");
                continue;
            }

            var kind = type.IsEnum ? "enum" : type.BaseType?.Name ?? "?";
            Console.WriteLine(
                $"=== {type.FullName} : {kind} public={type.IsPublic} sealed={type.IsSealed} abstract={type.IsAbstract}");

            const BindingFlags Flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                Console.WriteLine("  ctor(" + string.Join(", ", constructor.GetParameters().Select(P)) + ")");
            }

            if (type.IsEnum)
            {
                Console.WriteLine("  values: " + string.Join(", ", Enum.GetNames(type)));
                continue;
            }

            foreach (var property in type.GetProperties(Flags))
            {
                Console.WriteLine(
                    $"  prop {Short(property.PropertyType)} {property.Name}"
                    + $" get={property.GetMethod is not null} set={property.SetMethod is not null}");
            }

            foreach (var field in type.GetFields(Flags))
            {
                Console.WriteLine($"  field {Short(field.FieldType)} {field.Name}");
            }

            foreach (var ev in type.GetEvents(Flags))
            {
                Console.WriteLine($"  event {Short(ev.EventHandlerType!)} {ev.Name}");
            }

            foreach (var method in type.GetMethods(Flags)
                         .Where(m => !m.IsSpecialName)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                Console.WriteLine(
                    $"  method {Short(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(P))})");
            }
        }
    }

    private static string P(ParameterInfo parameter) =>
        Short(parameter.ParameterType) + " " + parameter.Name;

    private static string Short(Type type)
    {
        if (!type.IsGenericType) return type.Name;

        var name = type.Name[..type.Name.IndexOf('`')];

        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(Short)) + ">";
    }
}
