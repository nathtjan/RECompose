// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Services;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>The All services dialog: every service, upcoming first and past ones by month, filtered by name.</summary>
public sealed class ServiceBrowserViewModel : ObservableObject
{
    private readonly List<(ServiceChoice Choice, string Heading, string Name)> _all;
    private readonly string? _currentId;
    private string _filter = string.Empty;
    private ServicePickerRow? _selected;

    public ServiceBrowserViewModel(IEnumerable<Service> services, DateTime now, string? currentId)
    {
        var ordered = ServiceText.Ordered(services, now);
        var headings = ServiceText.MonthHeadings(ordered, now);
        _all = [.. ordered.Select((s, i) => (new ServiceChoice(s.Id, ServiceText.Title(s)), headings[i], s.Name))];
        _currentId = currentId;
        Rebuild();
    }

    public ObservableCollection<ServicePickerRow> Rows { get; } = [];

    public string ServiceFilter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? string.Empty))
                Rebuild();
        }
    }

    public ServicePickerRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                OnPropertyChanged(nameof(CanOpen));
        }
    }

    public bool CanOpen => _selected is not null;

    /// <summary>"52 services", "3 of 52 services", or "No service named “xyz”".</summary>
    public string Summary => ServicePickerRow.Summary(Rows.Count, _all.Count, _filter);

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var row in ServicePickerRow.Build(_all, _filter, _currentId))
            Rows.Add(row);
        Selected = Rows.FirstOrDefault(r => r.IsCurrent) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(Summary));
    }
}
