using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Hopper Tag Filter
//
// The intake hopper's allow/block list normally holds item identifiers only. This mod lets the
// same list hold item tags as well: the item types the tooltip shows in brackets ([Food],
// [Medical], [Contraband] ...), the stolen marker, and, for finer rules, the game's internal
// tags. Only the first two have names in the game's own text; internal tags are shown by ID.
// A tag entry is stored in the hopper's own filter string as "tag:<TAG_NAME>", next to the
// ordinary identifiers, so copy/paste, the chip list and saving all keep working through the
// game's own code. Without the mod such an entry simply never matches anything.
namespace HopperTagFilter
{
	public class Mod : IMod
	{
		public const string ID = "hopper_tag_filter";

		internal static ModLog Log;

		public void Init(ModManifest manifest)
		{
			Log = new ModLog(manifest);
		}

		public void OnEnable()
		{
			ModHook.OnModAssetsLoaded += Loc.OnModAssetsLoaded;
		}

		public void OnDisable()
		{
			ModHook.OnModAssetsLoaded -= Loc.OnModAssetsLoaded;
			TagPick.Active = false;
			TagChooser.Close();
			TagButton.Destroy();
		}
	}

	// ------------------------------------------------------------------ filter entries

	// A tag entry in the hopper's filter string: "tag:<TAG>", or "tag:<TAG>@<item id>" when the
	// tag was picked from an item, which then serves as the picture on the tag's chip.
	internal static class TagEntry
	{
		public const string PREFIX = "tag:";

		private const char SAMPLE = '@';

		public static bool Is(string entry)
		{
			return entry != null && entry.Length > PREFIX.Length && entry.StartsWith(PREFIX, StringComparison.Ordinal);
		}

		public static string TagOf(string entry)
		{
			int at = entry.IndexOf(SAMPLE, PREFIX.Length);
			return (at < 0) ? entry.Substring(PREFIX.Length) : entry.Substring(PREFIX.Length, at - PREFIX.Length);
		}

		public static string SampleOf(string entry)
		{
			int at = entry.IndexOf(SAMPLE, PREFIX.Length);
			return (at < 0 || at == entry.Length - 1) ? null : entry.Substring(at + 1);
		}

		public static string Make(string tag, string sampleItemId = null)
		{
			bool usable = !string.IsNullOrEmpty(sampleItemId) && sampleItemId.IndexOf(';') < 0 && sampleItemId.IndexOf(SAMPLE) < 0;
			return usable ? (PREFIX + tag + SAMPLE + sampleItemId) : (PREFIX + tag);
		}

		// The tags among a filter list's entries.
		public static HashSet<string> Tags(IEnumerable<string> list)
		{
			HashSet<string> tags = new HashSet<string>();
			foreach (string entry in list)
			{
				if (Is(entry))
				{
					tags.Add(TagOf(entry));
				}
			}
			return tags;
		}

		public static bool AnyMatch(HashSet<string> list, GameItem item)
		{
			foreach (string entry in list)
			{
				if (!Is(entry))
				{
					continue;
				}
				string tag = TagOf(entry);
				if (item.IsTag(tag) || item.IsGameItemType(tag))
				{
					return true;
				}
			}
			return false;
		}
	}

	// ------------------------------------------------------------------ names and text

	internal static class Loc
	{
		private static Dictionary<string, Dictionary<string, string>> tables;

		public static void OnModAssetsLoaded(LoadedMod mod)
		{
			if (mod != null && mod.Manifest != null && mod.Manifest.ID == Mod.ID)
			{
				tables = mod.Localization;
			}
		}

		public static bool TryGet(string key, out string value)
		{
			value = null;
			if (tables == null)
			{
				return false;
			}
			Dictionary<string, string> table;
			string code = LocHelper.GetCurrentLocaleCode();
			if (!string.IsNullOrEmpty(code) && tables.TryGetValue(code, out table) && table.TryGetValue(key, out value))
			{
				return true;
			}
			return tables.TryGetValue("en", out table) && table.TryGetValue(key, out value);
		}

		public static string Get(string key, string fallback, params object[] args)
		{
			string text;
			if (!TryGet(key, out text))
			{
				text = fallback;
			}
			if (args == null || args.Length == 0)
			{
				return text;
			}
			try
			{
				return string.Format(text, args);
			}
			catch (FormatException)
			{
				return text;
			}
		}

		public static string AddTags => Get("btn_add_tags", "Add tags");

		public static string PickFromItem => Get("btn_pick_from_item", "Pick from an item");

		public static string PickHint => Get("pick_hint", "Click an item to see its tags.");

		public static string PickItem(string name)
		{
			return Get("pick_item", "Click to see the tags of {0}.", name);
		}

		// Wherever the game already has the words, they are the game's, so they read right in
		// every language it has.
		public static string AllTypes => LocHelper.GetLocalizedOrDefault("Mechanic", "type_all_type", "All Types");

		public static string None => LocHelper.GetLocalizedOrDefault("Mechanic", "sec_data_none", "None");

		public static string SectionInternal => Get("section_internal", "Internal tags");

		// A category is called after its first tag, in the game's own words, so it reads right in
		// every language the game has. A "group.<key>" line in the mod's localization overrides it.
		public static string Group(TagGroup group)
		{
			string text;
			return TryGet("group." + group.Key, out text) ? text : TagNames.Display(group.Tags[0]);
		}

		public static string GroupOther
		{
			get
			{
				string text;
				return TryGet("group.other", out text) ? text : LocHelper.GetLocalizedOrDefault("Mechanic", "mech_husbandry_other", "Other");
			}
		}

		// Tags are written the way the game's tooltips write item types: [Food].
		public static string Chip(string tagName)
		{
			return "[" + tagName + "]";
		}
	}

	internal sealed class TagGroup
	{
		public readonly string Key;

		public readonly string[] Tags;

		public TagGroup(string key, params string[] tags)
		{
			Key = key;
			Tags = tags;
		}
	}

	internal static class TagNames
	{
		// Tags the tooltip shows as a bracketed marker although they are not item types. The game
		// has no separate name for them, so the mod's own localization names them.
		public static readonly List<string> Statuses = new List<string> { "STOLEN_TAG" };

		private static readonly HashSet<string> hidden = new HashSet<string>
		{
			"IS_OWNED_TAG", "PINNED_TAG", "CUSTOM_NAME_TAG", "CUSTOM_NAME_MODE_TAG", "ITEM_TYPE_STRING", "BOUGHT_PRICE_TAG"
		};

		// Item types: the game's list plus any type seen on an item (mods can add their own).
		private static HashSet<string> types;

		private static HashSet<string> Types
		{
			get
			{
				if (types == null)
				{
					types = new HashSet<string>(TypeHelper.ALL_TYPES_LIST);
				}
				return types;
			}
		}

		public static bool IsType(string tag)
		{
			return Types.Contains(tag);
		}

		// Item types, most traded first: ordered by how many of the game's customer definitions
		// buy the type plus how many sell items of it, then by how many items carry it
		// (read from the customer and item factories of game version 1.0.0.16).
		private static readonly string[] TypeOrder =
		{
			"FOOD", "MEDICAL", "HOUSEHOLD_GOOD", "MATERIAL", "SUBSTANCE", "CONTRABAND", "WEAPON", "LUXURY_ITEM", "TOOL", "WATER",
			"MELEE_WEAPON", "NARCOTIC", "DOCUMENT", "BASIC_MATERIAL", "MODULE", "AMMUNITION", "ALCOHOL", "PROCESSED_FOOD", "JUNK", "TREAT",
			"ENERGY_CELL", "CHEMICAL_SUPPLIES", "FILTER", "BREVAGE", "SEED", "ARMOR", "NODE", "WATER_PURIFICATION_SUPPLY", "WATER_RATION", "ACCESS_CARD",
			"MUNITION", "MAKESHIFT", "GUN_MOD", "MEDICAL_TOOL", "MACHINE", "LOTTERY", "STORAGE", "MIDLINE", "POISON", "HYDROPONIC_MODULE",
			"SUPPLY_CRATE", "ART", "FIREARM", "FARMING_SUPPLIES", "PRINTER_MODULE", "LOWTECH", "MODIFIED_SEED", "ALARM_MODULE", "EVAPORATOR_MODULE", "MODDABLE",
			"NOT_FOR_RESALE", "RESTRICTED_1", "RESTRICTED_2", "RESTRICTED_3", "HIGHEND", "RARE", "FRESH_FOOD", "REFINED_MATERIAL", "OXYGEN_SUPPLY", "ORGAN"
		};

		// The chooser's categories, grouped the way customers ask for things: types that show up
		// together in customers' buying lists share a group, led by its best-known type. Every
		// other type, and the stolen marker (which an item of any type can carry), goes under
		// "Other".
		public static readonly TagGroup[] Groups =
		{
			new TagGroup("household", "HOUSEHOLD_GOOD", "LUXURY_ITEM"),
			new TagGroup("food", "FOOD", "TREAT", "BREVAGE", "PROCESSED_FOOD"),
			new TagGroup("medical", "MEDICAL", "MEDICAL_TOOL"),
			new TagGroup("water", "WATER", "WATER_RATION", "WATER_PURIFICATION_SUPPLY", "FILTER"),
			new TagGroup("vice", "SUBSTANCE", "ALCOHOL", "NARCOTIC", "CHEMICAL_SUPPLIES"),
			new TagGroup("parts", "MATERIAL", "BASIC_MATERIAL", "MODULE", "NODE", "TOOL", "ENERGY_CELL"),
			new TagGroup("combat", "WEAPON", "MELEE_WEAPON", "AMMUNITION", "ARMOR", "MUNITION"),
			new TagGroup("contraband", "CONTRABAND")
		};

		// The item pictured on a tag's chip unless the tag was picked from an item. Tags without
		// an entry here (the stolen marker, types no item is defined with) get no picture.
		private static readonly Dictionary<string, string> DefaultSamples = new Dictionary<string, string>
		{
			{ "CONTRABAND", "dream_dust" },
			{ "FOOD", "nutrifruit" }, { "TREAT", "cat_bar" }, { "BREVAGE", "processed_juice" }, { "PROCESSED_FOOD", "processed_meat" },
			{ "MEDICAL", "salve" }, { "MEDICAL_TOOL", "surgery_tool" },
			{ "MATERIAL", "scrap_metal" }, { "BASIC_MATERIAL", "printer_plastic" }, { "MODULE", "system_module_performance" }, { "NODE", "node_medium" },
			{ "TOOL", "screwdriver" }, { "ENERGY_CELL", "energy_credit" },
			{ "WEAPON", "revolver" }, { "MELEE_WEAPON", "combat_knife" }, { "AMMUNITION", "heavy_pistol_ammo" }, { "ARMOR", "armor_lining" },
			{ "MUNITION", "flashbang_grenade" },
			{ "SUBSTANCE", "packet_red_cigarette" }, { "ALCOHOL", "nudka" }, { "NARCOTIC", "pink_injector" }, { "CHEMICAL_SUPPLIES", "common_chemical" },
			{ "HOUSEHOLD_GOOD", "toilet_paper" }, { "LUXURY_ITEM", "neuroactive_perfume" },
			{ "WATER", "bottled_water" }, { "WATER_RATION", "water_ration" }, { "WATER_PURIFICATION_SUPPLY", "water_test_strip" }, { "FILTER", "water_filter" },
			{ "DOCUMENT", "newspaper" }, { "JUNK", "junk" }, { "SEED", "kotton_seed" }, { "ACCESS_CARD", "blank_keycard" },
			{ "MAKESHIFT", "glass_shard_shiv" }, { "GUN_MOD", "silencer" }, { "MACHINE", "furnace" }, { "LOTTERY", "lottery_ticket" },
			{ "STORAGE", "storage_bay" }, { "MIDLINE", "shotgun" }, { "POISON", "zyanide_pill" }, { "HYDROPONIC_MODULE", "farm_module_nutrifruit" },
			{ "SUPPLY_CRATE", "med_box" }, { "ART", "cassette1" }, { "FIREARM", "glock_receiver" }, { "FARMING_SUPPLIES", "nutrient_tablet_container" },
			{ "PRINTER_MODULE", "printer_module_metal" }, { "LOWTECH", "handmade_pistol" }, { "MODIFIED_SEED", "nutrifruit_seed" },
			{ "ALARM_MODULE", "alarm_module_transmitter" }, { "EVAPORATOR_MODULE", "evaporator_module_solvent_recovery" }, { "MODDABLE", "smg" }
		};

		public static string DefaultSample(string tag)
		{
			string id;
			return DefaultSamples.TryGetValue(tag, out id) ? id : null;
		}

		// The members of a group that exist in this game: its item types, plus status tags.
		public static List<string> MembersOf(TagGroup group)
		{
			List<string> all = AllTypes();
			List<string> result = new List<string>();
			foreach (string tag in group.Tags)
			{
				if ((all.Contains(tag) || Statuses.Contains(tag)) && !result.Contains(tag))
				{
					result.Add(tag);
				}
			}
			return result;
		}

		// Item types that belong to no group, in TypeOrder.
		public static List<string> UngroupedTypes()
		{
			HashSet<string> grouped = new HashSet<string>();
			foreach (TagGroup group in Groups)
			{
				grouped.UnionWith(group.Tags);
			}
			return AllTypes().FindAll(t => !grouped.Contains(t));
		}

		// Every type the game knows, in TypeOrder; types this list has never heard of go last.
		public static List<string> AllTypes()
		{
			HashSet<string> known = new HashSet<string>(TypeHelper.ALL_TYPES_LIST);
			List<string> result = new List<string>();
			foreach (string type in TypeOrder)
			{
				if (known.Remove(type))
				{
					result.Add(type);
				}
			}
			foreach (string type in TypeHelper.ALL_TYPES_LIST)
			{
				if (!string.IsNullOrEmpty(type) && known.Remove(type))
				{
					result.Add(type);
				}
			}
			return result;
		}

		public static List<string> TypesOf(GameItem item)
		{
			List<string> result = new List<string>();
			List<string> itemTypes = item?.GetGameItemType();
			if (itemTypes == null)
			{
				return result;
			}
			foreach (string type in itemTypes)
			{
				if (!string.IsNullOrEmpty(type) && type.IndexOf(';') < 0 && !result.Contains(type))
				{
					Types.Add(type);
					result.Add(type);
				}
			}
			return result;
		}

		public static List<string> StatusesOf(GameItem item)
		{
			List<string> result = new List<string>();
			foreach (string tag in Statuses)
			{
				if (item != null && item.IsTag(tag))
				{
					result.Add(tag);
				}
			}
			return result;
		}

		// Every other enabled tag. These are the game's internal flags: nothing in the game's text
		// names them, so they are listed by ID.
		public static List<string> InternalTagsOf(GameItem item)
		{
			List<string> result = new List<string>();
			Dictionary<string, TagState> dict = item?.modifiedState?.dict;
			if (dict == null)
			{
				return result;
			}
			foreach (KeyValuePair<string, TagState> pair in dict)
			{
				string tag = pair.Key;
				TagState state = pair.Value;
				if (string.IsNullOrEmpty(tag) || state == null || !state.IsEnabled())
				{
					continue;
				}
				if (hidden.Contains(tag) || tag.StartsWith("HOPPER_", StringComparison.Ordinal) || tag.IndexOf(';') >= 0 || IsType(tag) || Statuses.Contains(tag))
				{
					continue;
				}
				result.Add(tag);
			}
			result.Sort(StringComparer.Ordinal);
			return result;
		}

		// The mod's own name for the tag if it has one, the game's name if the tag is an item
		// type, otherwise the bare ID.
		public static string Display(string tag)
		{
			string text;
			if (Loc.TryGet("tag." + tag, out text) && !string.IsNullOrEmpty(text))
			{
				return text;
			}
			if (IsType(tag))
			{
				return LocHelper.GetLocalizedOrDefault("Mechanic", "type_" + tag.ToLowerInvariant(), tag);
			}
			return tag;
		}
	}

	// ------------------------------------------------------------------ access to the game's hopper code

	internal static class Vanilla
	{
		public const string WINDOW_ID = "intake_hopper";

		public const string ADD_ITEMS_ID = "add_items";

		private static readonly MethodInfo rebuildList = AccessTools.Method(typeof(HopperHelper), "RebuildList");

		private static readonly MethodInfo hoveredStoreItem = AccessTools.Method(typeof(HopperHelper), "HoveredStoreItem");

		private static readonly FieldInfo openItem = AccessTools.Field(typeof(HopperHelper), "openItem");

		private static readonly FieldInfo pickingFor = AccessTools.Field(typeof(HopperHelper), "pickingFor");

		private static readonly MethodInfo describe = AccessTools.Method(typeof(HopperHelper), "Describe");

		private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

		// The picture the game's own item chips use for this item; null when there is none.
		public static Sprite ItemSprite(string itemId)
		{
			if (string.IsNullOrEmpty(itemId) || describe == null)
			{
				return null;
			}
			Sprite sprite;
			if (sprites.TryGetValue(itemId, out sprite))
			{
				return sprite;
			}
			try
			{
				if (DirectoryMaster.Has<GameItem>(itemId))
				{
					sprite = ((ValueTuple<string, Sprite>)describe.Invoke(null, new object[] { itemId })).Item2;
				}
			}
			catch (Exception e)
			{
				Mod.Log?.Warning("No picture for '" + itemId + "': " + e.Message);
			}
			sprites[itemId] = sprite;
			return sprite;
		}

		private static readonly FieldInfo pending = AccessTools.Field(typeof(HopperHelper), "pending");

		private static readonly FieldInfo batchQueued = AccessTools.Field(typeof(HopperHelper), "batchQueued");

		private static readonly FieldInfo batchRunning = AccessTools.Field(typeof(HopperHelper), "batchRunning");

		public static bool BatchReady => pending != null && batchQueued != null && batchRunning != null && pending.GetValue(null) is HashSet<GameItem>;

		public static HashSet<GameItem> Pending => pending.GetValue(null) as HashSet<GameItem>;

		public static void SetBatchQueued(bool value)
		{
			batchQueued.SetValue(null, value);
		}

		public static void SetBatchRunning(bool value)
		{
			batchRunning.SetValue(null, value);
		}

		public static bool Ready => rebuildList != null && hoveredStoreItem != null && openItem != null && pickingFor != null;

		public static CustomUIWindow Window()
		{
			CustomUIWindow window = (CustomUIManager.Instance != null) ? CustomUIManager.Instance.GetWindow(WINDOW_ID) : null;
			return (window != null && window.IsAlive) ? window : null;
		}

		public static GameItem OpenItem => openItem?.GetValue(null) as GameItem;

		public static GameItem PickingFor => pickingFor?.GetValue(null) as GameItem;

		public static GameItem HoveredStoreItem()
		{
			return hoveredStoreItem?.Invoke(null, null) as GameItem;
		}

		public static void RebuildList()
		{
			rebuildList?.Invoke(null, null);
		}

		public static bool IsGone(GameItem item)
		{
			if (item == null)
			{
				return true;
			}
			GameItemElement element = item as GameItemElement;
			return element != null && element.IsDestroyed();
		}

		// The game's own "Add items" button toggles its pick mode; pressing it is how this mod
		// starts and stops picking, so Escape handling, tooltips and input guards stay the game's.
		public static void PressAddItems()
		{
			Button button = Window()?.Get(ADD_ITEMS_ID)?.button;
			if (button != null)
			{
				button.onClick.Invoke();
			}
		}

		public static void PlayClick()
		{
			AudioManager.Instance?.Play("interface_click");
		}
	}

	// ------------------------------------------------------------------ "Add tags" pick mode

	internal static class TagPick
	{
		// True while the game's item pick mode is running on behalf of the "Add tags" button.
		public static bool Active;

		public static bool CanPick(GameItem hit)
		{
			return hit != null && !HopperHelper.IsHopper(hit);
		}

		// The hopper window's "Add tags" button: stops a running pick, otherwise opens the type list.
		public static void OnButton()
		{
			if (Vanilla.Window() == null)
			{
				return;
			}
			if (HopperHelper.IsBusy)
			{
				bool wasPickingTags = Active;
				Vanilla.PressAddItems();
				if (wasPickingTags)
				{
					return;
				}
			}
			else
			{
				Vanilla.PlayClick();
			}
			GameItem open = Vanilla.OpenItem;
			if (!Vanilla.IsGone(open))
			{
				TagChooser.Open(open, null);
			}
		}

		// The chooser's "Pick from an item" button: borrow the game's pick mode.
		public static void Start()
		{
			if (Vanilla.Window() == null)
			{
				return;
			}
			TagChooser.Close();
			Active = true;
			if (HopperHelper.IsBusy)
			{
				Vanilla.PlayClick();
			}
			else
			{
				Vanilla.PressAddItems();
				if (!HopperHelper.IsBusy)
				{
					Active = false;
				}
			}
			TagButton.Refresh();
		}
	}

	internal static class TagButton
	{
		private static CustomUIElement element;

		public static void Ensure(CustomUIWindow window)
		{
			if (window == null || (element != null && element.IsAlive))
			{
				return;
			}
			element = null;
			CustomUIElement addItems = window.Get(Vanilla.ADD_ITEMS_ID);
			RectTransform row = addItems?.root?.transform.parent as RectTransform;
			if (row == null || CustomUIManager.Instance == null)
			{
				return;
			}
			CustomUIBuilder builder = new CustomUIBuilder(CustomUIManager.Instance, window, row);
			builder.AddButton(Loc.AddTags, TagPick.OnButton);
			element = builder.Last;
			if (element == null || !element.IsAlive)
			{
				element = null;
				return;
			}
			LayoutElement layout = addItems.root.GetComponent<LayoutElement>();
			if (layout != null && layout.preferredHeight > 0f)
			{
				element.SetPreferredSize(-1f, layout.preferredHeight);
			}
			element.SetVisible(addItems.root.activeSelf);
			Refresh();
			CustomUIBuilder.RebuildLayout(window.Rect);
		}

		public static void SetVisible(bool visible)
		{
			if (element != null && element.IsAlive)
			{
				element.SetVisible(visible);
			}
		}

		public static void Refresh()
		{
			bool picking = TagPick.Active && HopperHelper.IsBusy;
			if (element != null && element.IsAlive)
			{
				element.SetText(picking ? LocHelper.GetLocalizedUI("ui_storage_coordinator_stop") : Loc.AddTags);
			}
			if (picking)
			{
				Vanilla.Window()?.Get(Vanilla.ADD_ITEMS_ID)?.SetText(LocHelper.GetLocalizedMechanic("mech_hopper_btn_add_items"));
			}
		}

		public static void Destroy()
		{
			if (element != null && element.root != null)
			{
				UnityEngine.Object.Destroy(element.root);
			}
			element = null;
		}
	}

	// ------------------------------------------------------------------ tag chooser window

	internal static class TagChooser
	{
		private const string WINDOW_ID = "hopper_tag_filter_chooser";

		private const string OTHER = "other";

		// Narrow for the category list; an item's own tags include long internal names.
		private const float WIDTH = 360f;

		private const float WIDTH_ITEM = 460f;

		private const float HEIGHT = 760f;

		private const float ARROW = 16f;

		// The gap the game's tick boxes leave before their caption.
		private const float GAP = 12f;

		private const float BOX = 32f;

		private static GameItem intake;

		// Categories unfolded in the chooser; kept for the session.
		private static readonly HashSet<string> expanded = new HashSet<string>();

		// The category lines of the window that is open now.
		private static readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();

		private sealed class Row
		{
			public string Key;

			public string Name;

			public List<string> Members;

			public bool Selectable;

			public Image Arrow;

			public CustomUIElement Box;

			public CustomUIElement Label;

			public RectTransform Body;
		}

		// With no sample item the window lists the tag categories, each one line that can be
		// ticked as a whole or unfolded; with one it lists that item's own tags.
		public static void Open(GameItem intakeItem, GameItem sample)
		{
			CustomUIManager manager = CustomUIManager.Instance;
			if (manager == null || Vanilla.IsGone(intakeItem))
			{
				return;
			}
			HashSet<string> listed = TagEntry.Tags(HopperHelper.GetFilterList(intakeItem));
			string title = (sample == null) ? Loc.AllTypes : sample.GetDisplayName(true);
			// Reopening for another item keeps the window where it is.
			CustomUIWindow previous = manager.GetWindow(WINDOW_ID);
			float width = (sample == null) ? WIDTH : WIDTH_ITEM;
			Vector2 position = (previous != null && previous.IsAlive && previous.Rect != null) ? previous.Rect.anchoredPosition : PositionBeside(Vanilla.Window(), width);
			CustomUIBuilder builder = manager.CreateWindow(WINDOW_ID, title, "paper", null, CustomUIManager.Style.Machinery)
				.SetSize(width, HEIGHT)
				.SetDraggable(true)
				.SetCloseOnEscape()
				.OnClose(delegate { intake = null; });
			intake = intakeItem;
			rows.Clear();
			builder.BeginRow(6f, TextAnchor.MiddleLeft);
			builder.AddButton(Loc.PickFromItem, TagPick.Start).WithTooltip(Loc.PickHint);
			builder.End();
			builder.AddSeparator();
			builder.BeginScrollFlexible(300f);
			if (sample != null)
			{
				AddItemTags(builder, listed, sample);
			}
			else
			{
				foreach (TagGroup group in TagNames.Groups)
				{
					AddCategory(builder, listed, group.Key, Loc.Group(group), TagNames.MembersOf(group), true);
				}
				List<string> other = new List<string>(TagNames.Statuses);
				other.AddRange(TagNames.UngroupedTypes());
				AddCategory(builder, listed, OTHER, Loc.GroupOther, other, false);
			}
			builder.End();
			builder.SetPosition(position);
			builder.Show();
		}

		// The tags one item carries: what the tooltip shows first, then the game's internal tags.
		private static void AddItemTags(CustomUIBuilder builder, HashSet<string> listed, GameItem sample)
		{
			List<string> shown = TagNames.StatusesOf(sample);
			shown.AddRange(TagNames.TypesOf(sample));
			List<string> internals = TagNames.InternalTagsOf(sample);
			if (shown.Count == 0 && internals.Count == 0)
			{
				builder.AddLabel(Loc.None);
			}
			// Tags ticked here keep this item as the picture on their chip.
			string sampleId = sample.identifier;
			foreach (string tag in shown)
			{
				string picked = tag;
				builder.AddToggle(TagNames.Display(tag), listed.Contains(tag), delegate(bool on) { SetMany(new List<string> { picked }, on, sampleId); });
				HoverButton.Light(builder.Last);
			}
			if (internals.Count > 0)
			{
				builder.AddHeader(Loc.SectionInternal);
			}
			foreach (string tag in internals)
			{
				string picked = tag;
				builder.AddToggle(TagNames.Display(tag), listed.Contains(tag), delegate(bool on) { SetMany(new List<string> { picked }, on, sampleId); });
				HoverButton.Light(builder.Last);
			}
		}

		private static string TagId(string tag)
		{
			return "tag_" + tag;
		}

		private static CustomUIWindow Window()
		{
			CustomUIWindow window = (CustomUIManager.Instance != null) ? CustomUIManager.Instance.GetWindow(WINDOW_ID) : null;
			return (window != null && window.IsAlive) ? window : null;
		}

		private static int CountListed(List<string> tags, HashSet<string> listed)
		{
			int count = 0;
			foreach (string tag in tags)
			{
				if (listed.Contains(tag))
				{
					count++;
				}
			}
			return count;
		}

		// One line per category: a tick box for the whole category (when selectable), its name
		// and an arrow. The box ticks or clears everything in the category and unfolds it; the
		// name and the arrow only fold and unfold it.
		private static void AddCategory(CustomUIBuilder builder, HashSet<string> listed, string key, string name, List<string> members, bool selectable)
		{
			if (members.Count == 0)
			{
				return;
			}
			if (selectable && members.Count == 1)
			{
				// Nothing to unfold: an ordinary tick box.
				builder.AddToggle(name, listed.Contains(members[0]), delegate(bool on) { SetMany(members, on, null); });
				HoverButton.Light(builder.Last);
				return;
			}
			Row row = new Row { Key = key, Name = name, Members = members, Selectable = selectable };
			rows[key] = row;
			builder.BeginRow(GAP, TextAnchor.MiddleLeft);
			if (selectable)
			{
				builder.AddToggle(null, false, delegate(bool on)
				{
					SetMany(row.Members, on, null);
					CustomUIWindow window = Window();
					foreach (string tag in row.Members)
					{
						window?.Get(TagId(tag))?.SetBool(on);
					}
					expanded.Add(row.Key);
					Refresh(row);
				});
				row.Box = builder.Last;
			}
			builder.BeginRow(6f, TextAnchor.MiddleLeft);
			// The caption takes the rest of the line, so a click anywhere on it counts.
			RectTransform caption = builder.CurrentContainer;
			caption.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
			builder.AddLabel(name);
			row.Label = builder.Last;
			if (row.Label?.text != null)
			{
				// Same look as the tick boxes' own captions.
				row.Label.text.enableWordWrapping = false;
				row.Label.text.fontStyle = TMPro.FontStyles.UpperCase;
				LayoutElement size = row.Label.root.GetComponent<LayoutElement>();
				if (size != null)
				{
					size.minHeight = BOX;
				}
			}
			builder.AddImage(Art.Arrow(false), ARROW, ARROW + 4f * Art.ARROW_DROP);
			row.Arrow = builder.Last?.image;
			builder.End();
			builder.End();
			Action fold = delegate
			{
				Vanilla.PlayClick();
				if (!expanded.Remove(row.Key))
				{
					expanded.Add(row.Key);
				}
				Refresh(row);
			};
			HoverButton.Attach(caption.gameObject, new Graphic[] { row.Label?.text, row.Arrow }, fold);

			builder.BeginColumn(4f);
			row.Body = builder.CurrentContainer;
			VerticalLayoutGroup layout = row.Body.GetComponent<VerticalLayoutGroup>();
			if (layout != null)
			{
				// Under the caption when there is a tick box, one step in otherwise.
				layout.padding = new RectOffset(Mathf.RoundToInt(selectable ? (BOX + GAP) : 24f), 0, 0, 4);
			}
			foreach (string tag in members)
			{
				string member = tag;
				builder.AddToggle(TagNames.Display(tag), listed.Contains(tag), delegate(bool on)
				{
					SetMany(new List<string> { member }, on, null);
					Refresh(row);
				}, TagId(tag));
				HoverButton.Light(builder.Last);
			}
			builder.End();
			Apply(row, listed);
		}

		// Brings a category's line in step with the hopper's list and with whether it is unfolded.
		private static void Apply(Row row, HashSet<string> listed)
		{
			int count = CountListed(row.Members, listed);
			bool open = expanded.Contains(row.Key);
			if (row.Arrow != null)
			{
				row.Arrow.sprite = Art.Arrow(open);
			}
			if (row.Body != null)
			{
				row.Body.gameObject.SetActive(open);
			}
			if (row.Box != null && row.Box.toggle != null)
			{
				row.Box.SetBool(count == row.Members.Count);
				Art.SetPartial(row.Box.toggle, count > 0 && count < row.Members.Count);
			}
			// "Food and drink (1/4)" while only part of the category is ticked.
			string caption = row.Name;
			if (count > 0 && !row.Selectable)
			{
				caption += " (" + count + ")";
			}
			else if (count > 0 && count < row.Members.Count)
			{
				caption += " (" + count + "/" + row.Members.Count + ")";
			}
			row.Label?.SetText(caption);
		}

		private static void Refresh(Row row)
		{
			CustomUIWindow window = Window();
			if (window == null || Vanilla.IsGone(intake))
			{
				return;
			}
			Apply(row, TagEntry.Tags(HopperHelper.GetFilterList(intake)));
			CustomUIBuilder.RebuildLayout(window.Rect);
		}

		public static void Close()
		{
			CustomUIWindow window = Window();
			if (window != null)
			{
				window.Close();
			}
			intake = null;
		}

		// Adds or removes tags in the hopper's list. A tag is listed once, whatever item it was
		// picked from.
		private static void SetMany(List<string> tags, bool on, string sampleItemId)
		{
			if (Vanilla.IsGone(intake))
			{
				Close();
				return;
			}
			List<string> list = HopperHelper.GetFilterList(intake);
			foreach (string tag in tags)
			{
				list.RemoveAll(e => TagEntry.Is(e) && TagEntry.TagOf(e) == tag);
				if (on)
				{
					list.Add(TagEntry.Make(tag, sampleItemId));
				}
			}
			HopperHelper.SetFilterList(intake, list);
			Vanilla.PlayClick();
			Vanilla.RebuildList();
		}

		// Next to the hopper window when there is room, otherwise overlapping it slightly.
		private static Vector2 PositionBeside(CustomUIWindow hopper, float width)
		{
			if (hopper == null || hopper.Rect == null)
			{
				return Vector2.zero;
			}
			Vector2 position = hopper.Rect.anchoredPosition;
			RectTransform parent = hopper.Rect.parent as RectTransform;
			float half = (parent != null && parent.rect.width > 0f) ? parent.rect.width * 0.5f : 960f;
			float offset = hopper.Rect.rect.width * 0.5f + width * 0.5f + 12f;
			if (position.x + offset + width * 0.5f <= half)
			{
				return new Vector2(position.x + offset, position.y);
			}
			if (position.x - offset - width * 0.5f >= -half)
			{
				return new Vector2(position.x - offset, position.y);
			}
			return position + new Vector2(48f, -48f);
		}
	}

	// ------------------------------------------------------------------ drawn pieces

	// The few pictures the game has no sprite for, drawn pixel by pixel in the palette of its
	// machinery windows (the tick box here is the game's "radio" sprite, 16 x 16, shown at 2x).
	internal static class Art
	{
		private static Sprite partialNormal;

		private static Sprite partialHover;

		private static Sprite arrowRight;

		private static Sprite arrowDown;

		private static readonly Color32 Clear = new Color32(0, 0, 0, 0);

		private static readonly Color32 Outline = Hex(0x292234);

		private static readonly Color32 Slot = Hex(0x64343b);

		private static readonly Color32 Lit = Hex(0x9cdb73);

		// Texture pixels; each is two screen units, like the rest of the game's window art.
		public const int ARROW_DROP = 1;

		private static readonly string[] ArrowShape =
		{
			"..X.....",
			"..XX....",
			"..XXX...",
			"..XXXX..",
			"..XXXX..",
			"..XXX...",
			"..XX....",
			"..X....."
		};

		private static Color32 Hex(int rgb)
		{
			return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
		}

		// pixel(x, y) with y counted from the top.
		private static Sprite Draw(int width, int height, Func<int, int, Color32> pixel)
		{
			Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
			texture.filterMode = FilterMode.Point;
			texture.wrapMode = TextureWrapMode.Clamp;
			Color32[] pixels = new Color32[width * height];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					pixels[(height - 1 - y) * width + x] = pixel(x, y);
				}
			}
			texture.SetPixels32(pixels);
			texture.Apply(false, true);
			return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), width);
		}

		// The game's unlit tick box with a lit rim two pixels wide, its face unchanged: some, not
		// all, are ticked.
		private static Sprite PartialBox(Color32 face)
		{
			return Draw(16, 16, delegate(int x, int y)
			{
				bool edgeX = x == 0 || x == 15;
				bool edgeY = y == 0 || y == 15;
				if (edgeX && edgeY)
				{
					return Clear;
				}
				if (edgeX || edgeY)
				{
					return Outline;
				}
				if (x >= 6 && x <= 9 && y == 6)
				{
					return Outline;
				}
				if (x >= 6 && x <= 9 && (y == 7 || y == 8))
				{
					return Slot;
				}
				return (x <= 2 || x >= 13 || y <= 2 || y >= 13) ? Lit : face;
			});
		}

		public static Sprite Arrow(bool down)
		{
			// Drawn ARROW_DROP pixels below the middle of its picture: centred on the line it
			// sits visibly higher than the text beside it.
			if (arrowRight == null)
			{
				arrowRight = Draw(8, 8 + 2 * ARROW_DROP, (x, y) => (y >= 2 * ARROW_DROP && ArrowShape[y - 2 * ARROW_DROP][x] == 'X') ? (Color32)Color.white : Clear);
			}
			if (arrowDown == null)
			{
				arrowDown = Draw(8, 8 + 2 * ARROW_DROP, (x, y) => (y >= 2 * ARROW_DROP && ArrowShape[x][y - 2 * ARROW_DROP] == 'X') ? (Color32)Color.white : Clear);
			}
			return down ? arrowDown : arrowRight;
		}

		// Switches a tick box between the game's unlit look and the half-lit one. A ticked box
		// shows the game's lit sprite on top either way.
		public static void SetPartial(Toggle toggle, bool partial)
		{
			Image box = toggle.targetGraphic as Image;
			if (box == null)
			{
				return;
			}
			if (partialNormal == null)
			{
				partialNormal = PartialBox(Hex(0x79746e));
			}
			if (partialHover == null)
			{
				partialHover = PartialBox(Hex(0xb1a890));
			}
			box.sprite = partial ? partialNormal : MachineryStyle.RadioOff;
			SpriteState state = toggle.spriteState;
			state.highlightedSprite = partial ? partialHover : MachineryStyle.RadioOffHover;
			toggle.spriteState = state;
		}
	}

	// Makes a line behave like a button: its caption lights up under the pointer, dims while
	// pressed, and an action runs on a left click.
	internal sealed class HoverButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
	{
		private Graphic[] lit;

		private Action onClick;

		private bool inside;

		private bool pressed;

		public static void Attach(GameObject target, Graphic[] lit, Action onClick)
		{
			if (target == null)
			{
				return;
			}
			// An invisible surface over the whole rectangle, so the gaps and the empty stretch
			// after the text react as well.
			Graphic surface = target.GetComponent<Graphic>();
			if (surface == null)
			{
				Image image = target.AddComponent<Image>();
				image.color = new Color(0f, 0f, 0f, 0f);
				surface = image;
			}
			surface.raycastTarget = true;
			HoverButton button = target.AddComponent<HoverButton>();
			button.lit = lit;
			button.onClick = onClick;
			button.Paint();
		}

		// Stretches a tick box over its whole line and makes its caption light up with it. The
		// click itself is the tick box's own.
		public static void Light(CustomUIElement toggle)
		{
			if (toggle != null && toggle.root != null && toggle.text != null)
			{
				toggle.SetFlexible(1f, -1f);
				Attach(toggle.root, new Graphic[] { toggle.text }, null);
			}
		}

		private void Paint()
		{
			Color color = pressed ? MachineryStyle.LABEL_PLATE : (inside ? MachineryStyle.TAB_TEXT_HOVER : MachineryStyle.BODY_TEXT);
			if (lit == null)
			{
				return;
			}
			foreach (Graphic graphic in lit)
			{
				if (graphic != null)
				{
					graphic.color = color;
				}
			}
		}

		public void OnPointerEnter(PointerEventData eventData)
		{
			inside = true;
			Paint();
		}

		public void OnPointerExit(PointerEventData eventData)
		{
			inside = false;
			Paint();
		}

		public void OnPointerDown(PointerEventData eventData)
		{
			pressed = eventData.button == PointerEventData.InputButton.Left;
			Paint();
		}

		public void OnPointerUp(PointerEventData eventData)
		{
			pressed = false;
			Paint();
		}

		public void OnPointerClick(PointerEventData eventData)
		{
			if (eventData.button == PointerEventData.InputButton.Left && onClick != null)
			{
				onClick();
			}
		}

		private void OnDisable()
		{
			inside = false;
			pressed = false;
			Paint();
		}
	}

	// ------------------------------------------------------------------ patches

	// The filter itself: an item is "listed" when its identifier is listed (the game's rule) or
	// when it carries any listed tag.
	[HarmonyPatch(typeof(HopperHelper), "Passes")]
	internal static class PassesPatch
	{
		private static void Postfix(int mode, HashSet<string> list, GameItem item, ref bool __result)
		{
			if (mode == 0 || list == null || item == null)
			{
				return;
			}
			bool listed = (mode == 1) ? __result : !__result;
			if (listed || !TagEntry.AnyMatch(list, item))
			{
				return;
			}
			__result = mode == 1;
		}
	}

	// Chip text and picture for tag entries; the game would otherwise look the entry up as an item.
	[HarmonyPatch(typeof(HopperHelper), "Describe")]
	internal static class DescribePatch
	{
		private static bool Prefix(string identifier, ref ValueTuple<string, Sprite> __result)
		{
			if (!TagEntry.Is(identifier))
			{
				return true;
			}
			string tag = TagEntry.TagOf(identifier);
			Sprite sprite = Vanilla.ItemSprite(TagEntry.SampleOf(identifier) ?? TagNames.DefaultSample(tag));
			__result = new ValueTuple<string, Sprite>(Loc.Chip(TagNames.Display(tag)), sprite);
			return false;
		}
	}

	[HarmonyPatch(typeof(HopperHelper), "OpenWindow")]
	internal static class OpenWindowPatch
	{
		private static void Postfix()
		{
			CustomUIWindow window = Vanilla.Window();
			if (window != null && Vanilla.Ready)
			{
				TagButton.Ensure(window);
			}
		}
	}

	// The game hides its "Add items" button while the filter is "everything"; follow it. Done
	// before the game's code runs because that code then fits the window height to its content.
	[HarmonyPatch(typeof(HopperHelper), "FillList")]
	internal static class FillListPatch
	{
		private static void Prefix()
		{
			GameItem open = Vanilla.OpenItem;
			TagButton.SetVisible(open != null && HopperHelper.GetFilterMode(open) != 0);
		}
	}

	[HarmonyPatch(typeof(HopperHelper), "RefreshLink")]
	internal static class RefreshLinkPatch
	{
		private static void Postfix()
		{
			TagButton.Refresh();
		}
	}

	[HarmonyPatch(typeof(HopperHelper), "StopPick")]
	internal static class StopPickPatch
	{
		private static void Postfix()
		{
			if (!HopperHelper.IsBusy)
			{
				TagPick.Active = false;
				TagButton.Refresh();
			}
		}
	}

	[HarmonyPatch(typeof(HopperHelper), "ResetWindowState")]
	internal static class ResetWindowStatePatch
	{
		private static void Postfix()
		{
			TagPick.Active = false;
			TagChooser.Close();
		}
	}

	// Hover text while picking for tags.
	[HarmonyPatch(typeof(HopperHelper), "PickVerdict")]
	internal static class PickVerdictPatch
	{
		private static void Postfix(GameItem hit, ref string message, ref bool __result)
		{
			if (!TagPick.Active)
			{
				return;
			}
			if (hit == null)
			{
				message = Loc.PickHint;
				__result = false;
			}
			else if (TagPick.CanPick(hit))
			{
				message = Loc.PickItem(hit.GetDisplayName(true));
				__result = true;
			}
		}
	}

	// Which hopper gets an item that several hoppers would take.
	//
	// Each hopper moves everything its own filter passes, so when two intake hoppers in the same
	// place both match an item (a combat knife is both "Weapon" and "Household Good"), the one
	// that runs first takes it. The game's nightly run and its overflow routing go by the
	// "run order" slider, lowest first, older hopper first on a tie. Its batch for "run when an
	// item is added / when the hopper is moved" does not: it runs the hoppers in whatever order
	// they happened to be queued. This runs that batch in the same order as the nightly one, so
	// one rule holds everywhere. Otherwise it is the game's own RunPending.
	internal static class Batch
	{
		private const int MAX_PER_BATCH = 64;

		public static int Compare(GameItem a, GameItem b)
		{
			int byOrder = HopperHelper.GetOrder(a).CompareTo(HopperHelper.GetOrder(b));
			return (byOrder != 0) ? byOrder : a.uniqueId.CompareTo(b.uniqueId);
		}

		public static IEnumerator Run()
		{
			yield return null;
			Vanilla.SetBatchQueued(false);
			Vanilla.SetBatchRunning(true);
			try
			{
				List<GameItem> intakes = new List<GameItem>();
				foreach (GameItem intake in Vanilla.Pending)
				{
					if (!Vanilla.IsGone(intake) && GeneralHelper.IsItemOwned(intake))
					{
						intakes.Add(intake);
					}
				}
				intakes.Sort(Compare);
				int moved = 0;
				for (int i = 0; i < intakes.Count && i < MAX_PER_BATCH; i++)
				{
					moved += HopperHelper.RunAndRecord(intakes[i]);
				}
				if (moved > 0)
				{
					EmporiumAudioPlayer.Instance?.PlayUnMatched();
				}
			}
			finally
			{
				Vanilla.SetBatchRunning(false);
				Vanilla.Pending.Clear();
			}
		}
	}

	[HarmonyPatch(typeof(HopperHelper), "RunPending")]
	internal static class RunPendingPatch
	{
		private static bool Prefix(ref IEnumerator __result)
		{
			if (!Vanilla.BatchReady)
			{
				return true;
			}
			__result = Batch.Run();
			return false;
		}
	}

	// A click on an item while picking for tags opens the chooser instead of listing the item.
	[HarmonyPatch(typeof(HopperHelper), "HandleInput")]
	internal static class HandleInputPatch
	{
		private static bool Prefix()
		{
			if (!TagPick.Active)
			{
				return true;
			}
			if (!HopperHelper.IsBusy)
			{
				TagPick.Active = false;
				return true;
			}
			if (!Input.GetMouseButtonDown(0))
			{
				return true;
			}
			GameItem hit = Vanilla.HoveredStoreItem();
			GameItem target = Vanilla.PickingFor;
			if (!TagPick.CanPick(hit) || Vanilla.IsGone(target))
			{
				return true;
			}
			Vanilla.PressAddItems();
			TagPick.Active = false;
			EmporiumAudioPlayer.Instance?.PlayUnMatched();
			TagChooser.Open(target, hit);
			return false;
		}
	}
}
