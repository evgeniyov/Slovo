using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Slovo.App.ViewModels;

namespace Slovo.App;

/// <summary>
/// Finds the view for a view model by name: <c>Slovo.App.ViewModels.Screens.DictionaryViewModel</c>
/// is shown by <c>Slovo.App.Views.Screens.DictionaryView</c>.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    static readonly Dictionary<Type, Type?> Cache = [];

    public Control? Build(object? data)
    {
        if (data is null)
        {
            return null;
        }
        var viewType = ViewTypeFor(data.GetType());
        return viewType is null
            ? new TextBlock { Text = "No view for " + data.GetType().Name }
            : (Control?)Activator.CreateInstance(viewType);
    }

    public bool Match(object? data) => data is ViewModelBase && ViewTypeFor(data.GetType()) is not null;

    static Type? ViewTypeFor(Type modelType)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(modelType, out var viewType))
            {
                var name = modelType.FullName;
                viewType = null;
                if (name is not null && name.EndsWith("ViewModel", StringComparison.Ordinal))
                {
                    name = name.Replace(".ViewModels", ".Views", StringComparison.Ordinal)[..^"ViewModel".Length] + "View";
                    var candidate = modelType.Assembly.GetType(name);
                    viewType = candidate is not null && typeof(Control).IsAssignableFrom(candidate) ? candidate : null;
                }
                Cache[modelType] = viewType;
            }
            return viewType;
        }
    }
}
