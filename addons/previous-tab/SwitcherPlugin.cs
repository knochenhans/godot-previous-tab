using Godot;
using Godot.Collections;
using System;
using System.Linq;

[Tool]
public partial class SwitcherPlugin : EditorPlugin
{
    private TabContainer scriptsTabContainer;
    private ItemList scriptsItemList;
    private TabBar scenesTabBar;
    private Switcher switcher;

    private Key baseShKey;
    private bool readyForHistory = false;
    private Control lastControl = null;

    public override void _EnterTree()
    {
        if (OS.HasFeature("macos"))
        {
            baseShKey = Key.Alt;
        }
        else
        {
            ResetDefaultTabsShortcuts();
            baseShKey = Key.Ctrl;
        }

        SceneChanged += OnSceneChanged;

        var scriptEditor = EditorInterface.Singleton.GetScriptEditor();
        scriptsTabContainer = FirstOrNull(scriptEditor.FindChildren("*", "TabContainer", true, false).Cast<TabContainer>());
        scriptsItemList = FirstOrNull(scriptEditor.FindChildren("*", "ItemList", true, false).Cast<ItemList>());
        scenesTabBar = GetScenesTabBar();

        if (scriptsTabContainer != null)
        {
            scriptsTabContainer.TabChanged += OnScriptTabChanged;
            InitializeHistoryAsync();
        }

        switcher = new Switcher
        {
            EditorInterface = EditorInterface.Singleton,
            ScriptsTabContainer = scriptsTabContainer,
            BaseShKey = baseShKey
        };
        EditorInterface.Singleton.GetBaseControl().AddChild(switcher);
    }

    private async void InitializeHistoryAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        readyForHistory = true;
    }

    public override void _ExitTree()
    {
        SceneChanged -= OnSceneChanged;
        if (scriptsTabContainer != null)
        {
            scriptsTabContainer.TabChanged -= OnScriptTabChanged;
        }
        if (switcher != null)
        {
            switcher.QueueFree();
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey eventKey)
        {
            bool baseShKeyPressed = Input.IsKeyPressed(baseShKey);
            if (baseShKeyPressed && (eventKey.Keycode == Key.Tab || eventKey.Keycode == Key.Backtab))
            {
                if (!switcher.Visible)
                {
                    switcher.RaiseSwitcher();
                }
            }
        }
    }

    private void OnSceneChanged(Node node)
    {
        string path = null;
        if (node != null)
        {
            path = node.SceneFilePath;
        }
        if (!string.IsNullOrEmpty(path))
        {
            AddToHistory(new HistoryItemScene(
                path,
                scenesTabBar.GetTabIcon(scenesTabBar.CurrentTab),
                EditorInterface.Singleton
            ));
        }
    }

    private TabBar GetScenesTabBar()
    {
        VBoxContainer mainScreen = null;
        var children = EditorInterface.Singleton.GetBaseControl().FindChildren("*", "VBoxContainer", true, false);
        foreach (var c in children)
        {
            if (c is VBoxContainer vbox && vbox.Name == "MainScreen")
            {
                mainScreen = vbox;
                break;
            }
        }
        if (mainScreen != null)
        {
            var centralScreenBox = mainScreen.GetParent().GetParent();
            return FirstOrNull(centralScreenBox.FindChildren("*", "TabBar", true, false).Cast<TabBar>());
        }
        return null;
    }

    private void OnScriptTabChanged(long idx)
    {
        if (!readyForHistory) return;

        var control = scriptsTabContainer.GetTabControl((int)idx);
        if (control == lastControl) return;

        lastControl = control;

        if (control == null) return;

        if (control.GetClass() == "EditorHelp") return;

        if (control is not CodeEdit && !control.ToString().Contains("TextEditor")) return;

        GD.Print("Script tab changed, idx: ", idx);

        AddToHistory(new HistoryItemScript(
            GodotObject.WeakRef(control),
            scriptsTabContainer,
            scriptsItemList
        ));
    }

    private void ResetDefaultTabsShortcuts()
    {
        var editorSettings = EditorInterface.Singleton.GetEditorSettings();
        var defaultSh = editorSettings.Get("shortcuts").AsGodotArray();

        Action<string> checkSh = (shName) =>
        {
            bool exists = false;
            foreach (var sh in defaultSh)
            {
                var dict = sh.AsGodotDictionary();
                if (dict.ContainsKey("name") && dict["name"].AsString() == shName)
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                var newShortcut = new Dictionary { { "name", shName }, { "shortcuts", new Godot.Collections.Array() } };
                defaultSh.Add(newShortcut);
                editorSettings.Set("shortcuts", defaultSh);
            }
        };

        checkSh("editor/next_tab");
        checkSh("editor/prev_tab");
    }

    private void AddToHistory(HistoryItem el)
    {
        switcher.AddToHistory(el);
    }

    private T FirstOrNull<T>(System.Collections.Generic.IEnumerable<T> enumerable) where T : class
    {
        return enumerable.FirstOrDefault();
    }
}

public partial class Switcher : AcceptDialog
{
    public EditorInterface EditorInterface { get; set; }
    public TabContainer ScriptsTabContainer { get; set; }
    public Key BaseShKey { get; set; }

    private Tree historyTree;
    private TreeItem root;
    private HBoxContainer checkBoxes;

    private System.Collections.Generic.List<HistoryItem> history = new System.Collections.Generic.List<HistoryItem>();
    private System.Collections.Generic.List<string> filterTypes = new System.Collections.Generic.List<string>();

    public Switcher()
    {
        Title = "Switcher";

        var vb = new VBoxContainer();

        historyTree = new Tree
        {
            HideRoot = true,
            HideFolding = true
        };
        historyTree.ItemActivated += HandleConfirmed;
        historyTree.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        historyTree.FocusMode = Control.FocusModeEnum.None;
        root = historyTree.CreateItem();
        vb.AddChild(historyTree);

        checkBoxes = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.End
        };
        AddFilterCheckbox("script", true, AddFilter("script"));
        AddFilterCheckbox("scene", true, AddFilter("scene"));
        AddFilterCheckbox("doc", true, AddFilter("doc"));
        vb.AddChild(checkBoxes);

        AddChild(vb);

        GetOkButton().Hide();
    }

    public override void _Ready()
    {
        SetProcessInput(false);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey eventKey && eventKey.Keycode == BaseShKey)
        {
            if (!eventKey.Pressed)
            {
                HandleConfirmed();
                return;
            }
        }

        if (@event is InputEventKey k && k.Pressed)
        {
            if (k.Keycode == Key.Pageup || k.Keycode == Key.Up)
            {
                SelectPrev();
            }
            if (k.Keycode == Key.Pagedown || k.Keycode == Key.Down || k.Keycode == Key.Backtab)
            {
                SelectNext();
            }
            if (k.Keycode == Key.Tab)
            {
                if (k.ShiftPressed)
                {
                    SelectPrev();
                }
                else
                {
                    SelectNext();
                }
            }
        }
    }

    public void AddToHistory(HistoryItem el)
    {
        el.AddTo(history);
        if (history.Count > 20)
        {
            history.RemoveRange(20, history.Count - 20);
        }
    }

    public void RaiseSwitcher()
    {
        PopupCenteredRatio(0.3f);
        Callable.From(() => SetProcessInput(true)).CallDeferred();
        UpdateTree();
    }

    private void SelectNext()
    {
        var selected = historyTree.GetSelected();
        if (selected == null || root.GetChildCount() == 0) return;

        int idx = selected.GetIndex();
        idx = (int)Mathf.Wrap(idx + 1, 0, root.GetChildCount());
        root.GetChild(idx).Select(0);
        historyTree.EnsureCursorIsVisible();
    }

    private void SelectPrev()
    {
        var selected = historyTree.GetSelected();
        if (selected == null || root.GetChildCount() == 0) return;

        int idx = selected.GetIndex();
        idx = (int)Mathf.Wrap(idx - 1, 0, root.GetChildCount());
        root.GetChild(idx).Select(0);
        historyTree.EnsureCursorIsVisible();
    }

    private void HandleConfirmed()
    {
        var selected = historyTree.GetSelected();
        if (selected != null && selected.HasMeta("ref"))
        {
            var refObj = selected.GetMeta("ref").AsGodotObject();
            if (refObj is HistoryItem item)
            {
                item.Open();
            }
        }
        Hide();
    }

    private void UpdateTree()
    {
        ClearTreeItemChildren(root);

        HistoryItem firstHistoryItem = null;
        TreeItem itemToSelect = null;

        var historyCopy = new System.Collections.Generic.List<HistoryItem>(history);
        foreach (var el in historyCopy)
        {
            if (el.IsValid() && el.HasFilter(filterTypes))
            {
                var item = historyTree.CreateItem(root);
                el.Fill(item);
                item.SetMeta("ref", el);

                if (firstHistoryItem == null)
                {
                    firstHistoryItem = el;
                }
                else
                {
                    if (itemToSelect == null && firstHistoryItem.HasSameTypeAs(el))
                    {
                        itemToSelect = item;
                    }
                }
            }
            if (!el.IsValid())
            {
                history.Remove(el);
            }
        }

        if (itemToSelect != null)
        {
            itemToSelect.Select(0);
        }
        else if (root.GetChildCount() > 1)
        {
            root.GetChild(1).Select(0);
        }
        else if (root.GetChildCount() > 0)
        {
            root.GetChild(0).Select(0);
        }
        historyTree.EnsureCursorIsVisible();
    }

    private void ClearTreeItemChildren(TreeItem item)
    {
        if (item == null) return;
        foreach (var child in item.GetChildren())
        {
            item.RemoveChild(child);
            child.Free();
        }
    }

    private Action<bool> AddFilter(string filterName)
    {
        return (toggled) =>
        {
            filterTypes.Remove(filterName);
            if (toggled)
            {
                filterTypes.Add(filterName);
            }
            UpdateTree();
        };
    }

    private void AddFilterCheckbox(string cname, bool buttonPressed, Action<bool> onToggled)
    {
        var checkBox = new CheckBox
        {
            Text = cname
        };
        checkBox.Toggled += (toggled) => onToggled(toggled);
        checkBox.ButtonPressed = buttonPressed;
        checkBoxes.AddChild(checkBox);
    }
}

public partial class HistoryItem : RefCounted
{
    public void AddTo(System.Collections.Generic.List<HistoryItem> historyList)
    {
        var copy = new System.Collections.Generic.List<HistoryItem>(historyList);
        foreach (var el in copy)
        {
            if (el.Equals(this))
            {
                historyList.Remove(el);
            }
        }
        historyList.Insert(0, this);
    }

    public override bool Equals(object obj)
    {
        return false;
    }

    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    public virtual void Fill(TreeItem item) { }

    public virtual bool IsValid() => false;

    public virtual void Open() { }

    public virtual bool HasFilter(System.Collections.Generic.List<string> types) => true;

    public virtual bool HasSameTypeAs(HistoryItem another) => false;
}

public partial class HistoryItemScene : HistoryItem
{
    private EditorInterface editorInterface;
    private string scenePath;
    private Texture2D icon;

    public HistoryItemScene(string scenePath, Texture2D icon, EditorInterface editorInterface)
    {
        this.scenePath = scenePath;
        this.icon = icon;
        this.editorInterface = editorInterface;

        GD.Print("HistoryItemScene created with path: ", scenePath);
    }

    public override bool Equals(object obj)
    {
        if (obj is HistoryItemScene another)
        {
            return scenePath == another.scenePath;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return scenePath != null ? scenePath.GetHashCode() : 0;
    }

    public override void Fill(TreeItem item)
    {
        item.SetText(0, scenePath.GetFile().GetBaseName());
        item.SetIcon(0, icon);
    }

    public override bool IsValid()
    {
        var openScenes = editorInterface.GetOpenScenes();
        foreach (var s in openScenes)
        {
            if (s == scenePath) return true;
        }
        return false;
    }

    public override void Open()
    {
        if (IsValid())
        {
            editorInterface.OpenSceneFromPath(scenePath);
        }
    }

    public override bool HasFilter(System.Collections.Generic.List<string> types)
    {
        return types.Contains("scene");
    }

    public override bool HasSameTypeAs(HistoryItem another)
    {
        return another is HistoryItemScene;
    }
}

public partial class HistoryItemScript : HistoryItem
{
    private TabContainer scriptsTabContainer;
    private ItemList scriptsItemList;
    private WeakRef control;

    public HistoryItemScript(WeakRef control, TabContainer scriptsTabContainer, ItemList scriptsItemList)
    {
        this.control = control;
        this.scriptsTabContainer = scriptsTabContainer;
        this.scriptsItemList = scriptsItemList;

        var underlying = control.GetRef().AsGodotObject();
        GD.Print("HistoryItemScript created with control: ", underlying != null ? underlying.ToString() : "null");
    }

    public override bool Equals(object obj)
    {
        if (obj is HistoryItemScript another)
        {
            var thisObj = control.GetRef().AsGodotObject();
            var otherObj = another.control.GetRef().AsGodotObject();
            if (thisObj == null || otherObj == null) return false;
            return thisObj.Equals(otherObj);
        }
        return false;
    }

    public override int GetHashCode()
    {
        var val = control.GetRef().AsGodotObject();
        return val != null ? val.GetHashCode() : 0;
    }

    public override void Fill(TreeItem item)
    {
        if (control.GetRef().AsGodotObject() is Control controlObj)
        {
            int tabIdx = scriptsTabContainer.GetTabIdxFromControl(controlObj);
            int listItemIdx = FindItemListIdxByTabIdx(tabIdx);
            if (listItemIdx != -1)
            {
                item.SetText(0, scriptsItemList.GetItemText(listItemIdx));
                item.SetIcon(0, scriptsItemList.GetItemIcon(listItemIdx));
            }
        }
    }

    public override bool IsValid()
    {
        return control.GetRef().AsGodotObject() != null;
    }

    public override void Open()
    {
        if (control.GetRef().AsGodotObject() is Control controlObj)
        {
            int tabIdx = scriptsTabContainer.GetTabIdxFromControl(controlObj);
            int itemIdx = FindItemListIdxByTabIdx(tabIdx);
            if (itemIdx != -1)
            {
                if (!scriptsItemList.IsSelected(itemIdx))
                {
                    scriptsItemList.Select(itemIdx);
                    scriptsItemList.EmitSignal(ItemList.SignalName.ItemSelected, itemIdx);
                }
            }
        }
    }

    public override bool HasFilter(System.Collections.Generic.List<string> types)
    {
        if (control.GetRef().AsGodotObject() is not Control controlObj)
        {
            return false;
        }
        if (controlObj.ToString().Contains("EditorHelp"))
        {
            return types.Contains("doc");
        }
        else
        {
            return types.Contains("script");
        }
    }

    private int FindItemListIdxByTabIdx(int tabIdx)
    {
        for (int i = 0; i < scriptsItemList.ItemCount; i++)
        {
            var metadata = scriptsItemList.GetItemMetadata(i);
            if (metadata.VariantType != Variant.Type.Nil && metadata.AsInt32() == tabIdx)
            {
                return i;
            }
        }
        return -1;
    }

    public override bool HasSameTypeAs(HistoryItem another)
    {
        return another is HistoryItemScript;
    }
}