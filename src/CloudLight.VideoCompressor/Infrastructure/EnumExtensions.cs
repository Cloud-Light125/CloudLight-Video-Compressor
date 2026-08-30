using System.ComponentModel;
using System.Reflection;

namespace CloudLight.VideoCompressor.Infrastructure;

public static class EnumExtensions
{
    public static string GetDescription(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString(), BindingFlags.Public | BindingFlags.Static);
        return field?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? value.ToString();
    }
}
