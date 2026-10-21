using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Lumora.Gallery.ViewModels;

namespace Lumora.Gallery.ListView;

/// <summary>
/// DataGrid der Listenansicht mit denselben beiden Eigenschaften wie <see cref="Controls.GalleryListBox"/>:
/// (1) Zeilen sind im UI-Automation-Baum Blätter (<see cref="GalleryDataGridRow"/>), damit ein lauschender
/// Automation-Client nicht je Zelle Peers pflegt (CLAUDE.md-Regel aus 0.2.1); (2) die Auswahl wird zwischen
/// <see cref="GalleryItem.IsSelected"/> (ViewModel, maßgeblich) und der DataGrid-Auswahl in beide Richtungen
/// abgeglichen — auch für Zeilen, die wegen der Zeilen-Virtualisierung gerade keinen Container haben.
/// </summary>
public sealed class GalleryDataGrid : DataGrid
{
    private readonly HashSet<GalleryItem> _trackedItems = new();
    private bool _selectionSyncScheduled;

    private readonly HashSet<GalleryDataGridRow> _realizedRows = new();

    protected override DependencyObject GetContainerForItemOverride() => new GalleryDataGridRow();

    /// <summary>
    /// Eigener Peer statt <see cref="DataGridAutomationPeer"/>: dessen Item-Peers erzeugen je Zelle weitere Peers (bei
    /// lauschendem Automation-Client je Layoutdurchlauf aktualisiert). Hier sind nur die realisierten Zeilen Kinder, als Blätter.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new RowsOnlyGridPeer(this);

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is GalleryDataGridRow row)
        {
            _realizedRows.Remove(row);
        }

        base.ClearContainerForItemOverride(element, item);
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is GalleryDataGridRow;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (_selectionSyncScheduled)
        {
            SyncSelectionFromItems();
        }

        base.PrepareContainerForItemOverride(element, item);
        if (element is GalleryDataGridRow row)
        {
            _realizedRows.Add(row);
        }
    }

    /// <summary>Auswahl-Gesten des DataGrid (Klick, Umschalt-Bereich, Strg+A) erreichen auch Items ohne Zeile.</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems.OfType<GalleryItem>())
        {
            item.IsSelected = false;
        }

        foreach (var item in e.AddedItems.OfType<GalleryItem>())
        {
            item.IsSelected = true;
        }

        base.OnSelectionChanged(e);
    }

    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        TrackAll();
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Track(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                Untrack(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                Untrack(e.OldItems);
                Track(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                TrackAll();
                break;
        }
    }

    private void TrackAll()
    {
        foreach (var item in _trackedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        _trackedItems.Clear();
        Track(Items);
    }

    private void Track(IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.OfType<GalleryItem>())
        {
            if (_trackedItems.Add(item))
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    private void Untrack(IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.OfType<GalleryItem>())
        {
            if (_trackedItems.Remove(item))
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GalleryItem.IsSelected) || _selectionSyncScheduled)
        {
            return;
        }

        _selectionSyncScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, SyncSelectionFromItems);
    }

    private void SyncSelectionFromItems()
    {
        _selectionSyncScheduled = false;
        if (SelectionMode == DataGridSelectionMode.Single)
        {
            return;
        }

        var selected = new List<GalleryItem>();
        foreach (var item in Items.OfType<GalleryItem>())
        {
            if (item.IsSelected)
            {
                selected.Add(item);
            }
        }

        if (selected.Count == SelectedItems.Count && SelectedItems.OfType<GalleryItem>().All(i => i.IsSelected))
        {
            return;
        }

        // MultiSelector: Sammeländerung → ein SelectionChanged statt eines je Zeile.
        BeginUpdateSelectedItems();
        try
        {
            SelectedItems.Clear();
            foreach (var item in selected)
            {
                SelectedItems.Add(item);
            }
        }
        finally
        {
            EndUpdateSelectedItems();
        }
    }

    private sealed class RowsOnlyGridPeer : FrameworkElementAutomationPeer
    {
        public RowsOnlyGridPeer(GalleryDataGrid owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

        protected override string GetClassNameCore() => nameof(GalleryDataGrid);

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            var grid = (GalleryDataGrid)Owner;
            if (grid._realizedRows.Count == 0)
            {
                return null;
            }

            var peers = new List<AutomationPeer>(grid._realizedRows.Count);
            foreach (var row in grid._realizedRows.OrderBy(r => r.GetIndex()))
            {
                if (row.IsVisible && CreatePeerForElement(row) is { } peer)
                {
                    peers.Add(peer);
                }
            }

            return peers;
        }
    }
}

/// <summary>
/// Zeile der Listenansicht; ihr Automation-Peer ist ein Blatt (Name = Dateiname, keine Zellen als Kinder).
/// <see cref="DataGridRowAutomationPeer"/> ist versiegelt — deshalb ein eigener <see cref="FrameworkElementAutomationPeer"/>.
/// </summary>
public sealed class GalleryDataGridRow : DataGridRow
{
    protected override AutomationPeer OnCreateAutomationPeer() => new LeafRowPeer(this);

    private sealed class LeafRowPeer : FrameworkElementAutomationPeer
    {
        public LeafRowPeer(DataGridRow owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

        protected override string GetClassNameCore() => nameof(GalleryDataGridRow);

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override string GetNameCore()
            => (Owner as DataGridRow)?.DataContext is GalleryItem item ? item.Name : base.GetNameCore();
    }
}
