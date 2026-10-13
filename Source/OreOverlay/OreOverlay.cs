using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace OreOverlay
{
    public class OreOverlayGrid : MapComponent, ICellBoolGiver
    {
		// State checks toggled by the on-screen buttons
        public static bool m_ShowOres = false;
        public static bool m_ShowDeepOres = false;

		#region Colour Defines
		private static readonly Color m_DefaultOreColor = Color.green;
        private static readonly Color m_ChemfuelDeposit = CreateColor(194, 104, 45);
        private static readonly Color m_SteamVent = CreateColor(41, 68, 194);
        private static readonly Color m_HexDeposit = CreateColor(96, 194, 82);

		// Colours for ores whose own def data gives a poor or ambiguous colour. Everything else is discovered in FindOwnColor.
		private static readonly Dictionary<string, Color> m_ColorOverrides = new Dictionary<string, Color>()
		{
			{ "MineableSteel",      CreateColor(214, 141, 91) }, // Discovered beige, too close to silver
			{ "MineableUranium",      CreateColor(164, 189, 173) }, // Discovered dark grey, same as obsidian
			{ "MineableComponentsIndustrial",      CreateColor(194, 120, 24) }, // Discovered from the stuff colour, too close to gold
			// Coloured jade from a mod that isn't available for checking what discovery gives
			{ "MineableBlueJade",      CreateColor(44,96,165) },
			{ "MineableRedJade",      CreateColor(185,35,31) },
			{ "MineableWhiteJade",      CreateColor(255,255,255) },
			{ "MineableBlackJade",      CreateColor(32,32,32) },
			{ "MineableYellowJade",      CreateColor(230,194,44) },
		};

		// Colours by mined resource, for resources whose def data gives no usable colour (the icon average comes out grey).
		private static readonly Dictionary<string, Color> m_ResourceColorOverrides = new Dictionary<string, Color>()
		{
			{ "ComponentSpacer",      CreateColor(230,214,90) },
			{ "GravlitePanel",      CreateColor(150,120,230) },
		};

		// How good a colour source is, best first. Ores take the best colour found for their resource.
		private const int SourceOverride = 0;
		private const int SourceFleckColor = 1;
		private const int SourceRockColor = 2;
		private const int SourceStuffColor = 3;
		private const int SourceIcon = 4;
		private const int SourceNone = 5;
		private static readonly string[] SourceNames = { "override", "rock colorTwo", "rock colour", "resource stuff colour", "resource icon", "uiIconColor" };

		// Geysers and ponds. These win over ores on the same cell and can be hidden with the "show steam" option.
		private static readonly Dictionary<string, Color> m_SpecialThings = new Dictionary<string, Color>()
		{
			{ "SteamGeyser",      m_SteamVent },
			{ "VPE_ChemfuelPond",      m_ChemfuelDeposit },
			{ "VPE_HelixienGeyser",      m_HexDeposit },
			{ "VHGE_GasGeyser",      m_HexDeposit },
		};

		public Color Color => Color.white;
		#endregion

		// How often the grid is rescanned while the overlay is shown, and how often the deep scanner state is checked.
		private const int RefreshIntervalTicks = 500;
		private const int ScannerCheckIntervalTicks = 60;

		// Lookup tables indexed by ThingDef.index, built once from the def database.
		private static bool[] m_IsOreDef = null;
		private static bool[] m_IsSpecialDef = null;
		private static Color[] m_DefColor = null;

		private Map m_Map;

		// Variables used to control the overlay's data for display.
        private CellBoolDrawer m_Drawer;
		private ThingDef[] m_CellDef = null; // Ore or special thing on each cell, null if none
        private Color[] m_ColorMap = null;
		private bool[] m_CellVisible = null; // Precomputed result for GetCellBool

		// Refresh monitoring, per map
		private int m_LastRefreshTick = -1;
		private int m_LastScannerCheckTick = -1;
		private bool m_ActiveScanner = false;
		private bool m_Stale = true;
		private bool m_WasShown = false;

		// Initialiser
        public OreOverlayGrid(Map map) : base(map)
        {
			// Save the map for checking later
            m_Map = map;

            this.m_Drawer = new CellBoolDrawer((ICellBoolGiver)this, map.Size.x, map.Size.z, 3640, 0.33f);

			PrepareDefLookup(); // Normally already done on startup

			// Create the map. It is filled in on the first draw with the overlay shown.
			int NumCells = map.cellIndices.NumGridCells;
			this.m_CellDef = new ThingDef[NumCells];
			this.m_ColorMap = new Color[NumCells];
			this.m_CellVisible = new bool[NumCells];
        }

		#region Ore data refresh
		// This function prepares the lookup tables used to determine what is highlighted and in which colour. Update here to support new ores.
		public static void PrepareDefLookup()
		{
			if (m_IsOreDef != null)
				return;

			int DefCount = DefDatabase<ThingDef>.DefCount;
			bool[] IsOre = new bool[DefCount];
			bool[] IsSpecial = new bool[DefCount];
			Color[] DefColor = new Color[DefCount];

			foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (thingDef.index >= DefCount)
					continue;

				if (m_SpecialThings.TryGetValue(thingDef.defName, out Color SpecialColor))
				{
					IsSpecial[thingDef.index] = true;
					DefColor[thingDef.index] = SpecialColor;
				}
				else if (IsOreDef(thingDef))
				{
					IsOre[thingDef.index] = true;
				}
			}

			// Every ore gets the best colour found for its resource, so the same resource has the same colour everywhere,
			// e.g. VGE compressed steel and twisted steel get the vanilla steel colour and VEE jadeite the vanilla jade colour.
			Dictionary<ThingDef, Color> ResourceColor = new Dictionary<ThingDef, Color>();
			Dictionary<ThingDef, int> ResourceSource = new Dictionary<ThingDef, int>();
			Dictionary<ThingDef, string> ResourceOrigin = new Dictionary<ThingDef, string>();

			foreach (KeyValuePair<string, Color> Entry in m_ResourceColorOverrides)
			{
				ThingDef Resource = DefDatabase<ThingDef>.GetNamedSilentFail(Entry.Key);
				if (Resource == null)
					continue;

				ResourceColor[Resource] = Entry.Value;
				ResourceSource[Resource] = SourceOverride;
				ResourceOrigin[Resource] = "resource override";
			}

			int[] OwnSource = new int[DefCount];
			foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if ((thingDef.index >= DefCount) || !IsOre[thingDef.index])
					continue;

				DefColor[thingDef.index] = FindOwnColor(thingDef, out OwnSource[thingDef.index]);

				ThingDef Resource = thingDef.building.mineableThing;
				if (!ResourceSource.TryGetValue(Resource, out int BestSource) || (OwnSource[thingDef.index] < BestSource))
				{
					ResourceColor[Resource] = DefColor[thingDef.index];
					ResourceSource[Resource] = OwnSource[thingDef.index];
					ResourceOrigin[Resource] = thingDef.defName;
				}
			}

			foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if ((thingDef.index >= DefCount) || !IsOre[thingDef.index])
					continue;

				ThingDef Resource = thingDef.building.mineableThing;
				string Source = SourceNames[OwnSource[thingDef.index]];
				if (ResourceSource[Resource] < OwnSource[thingDef.index])
				{
					DefColor[thingDef.index] = ResourceColor[Resource];
					Source = "same resource as " + ResourceOrigin[Resource];
				}

				if (Prefs.DevMode && !m_ColorOverrides.ContainsKey(thingDef.defName))
					Log.Message("[Ore Overlay] " + thingDef.defName + ": colour " + DefColor[thingDef.index].ToString() + " from " + Source);
			}

			m_IsSpecialDef = IsSpecial;
			m_DefColor = DefColor;
			m_IsOreDef = IsOre;
		}

		// Picks the overlay colour of an ore from the override table or the def data of the rock or its resource.
		private static Color FindOwnColor(ThingDef thingDef, out int Source)
		{
			Source = SourceOverride;
			if (m_ColorOverrides.TryGetValue(thingDef.defName, out Color OverrideColor))
				return OverrideColor;

			// Resource rocks draw their flecks in colorTwo, e.g. vanilla ores, Jewelry's gems and Odyssey's obsidian.
			Source = SourceFleckColor;
			Color FleckColor = thingDef.graphicData?.colorTwo ?? Color.white;
			if (FleckColor != Color.white)
				return Brighten(FleckColor);

			// Some ores tint the whole rock instead, e.g. VPE's eltex. Grey is just the stone colour.
			Source = SourceRockColor;
			Color RockColor = thingDef.graphicData?.color ?? Color.white;
			if ((RockColor != Color.white) && !IsGrey(RockColor))
				return Brighten(RockColor);

			ThingDef Resource = thingDef.building.mineableThing;

			Source = SourceStuffColor;
			if ((Resource.stuffProps != null) && (Resource.stuffProps.color.a > 0f))
				return Brighten(Resource.stuffProps.color);

			// Last resort: the average colour of the resource icon.
			// This needs the graphics device, so it only works on the main thread.
			Source = SourceIcon;
			if (UnityData.IsInMainThread && TryGetAverageIconColor(Resource, out Color IconColor))
				return Brighten(IconColor);

			Source = SourceNone;
			return thingDef.uiIconColor;
		}

		private static bool IsGrey(Color color)
		{
			Color.RGBToHSV(color, out float H, out float S, out float V);
			return S < 0.15f;
		}

		// The def colours are meant for textures and look dull on the overlay. Lift coloured ones to the brightness of the
		// override table, but leave greys alone so dark ores like obsidian stay dark.
		private static Color Brighten(Color color)
		{
			if (IsGrey(color))
				return new Color(color.r, color.g, color.b, 1f);

			Color.RGBToHSV(color, out float H, out float S, out float V);
			return Color.HSVToRGB(H, S, Mathf.Max(V, 0.75f));
		}

		// Textures are not readable, so draw the icon scaled down into a render texture and read that back.
		private static bool TryGetAverageIconColor(ThingDef def, out Color Average)
		{
			Average = Color.white;

			Texture2D Icon = def.uiIcon;
			if ((Icon == null) || (Icon == BaseContent.BadTex))
				return false;

			const int Size = 16;
			RenderTexture Previous = RenderTexture.active;
			RenderTexture Target = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32);
			Texture2D Readback = new Texture2D(Size, Size, TextureFormat.ARGB32, false);

			try
			{
				Graphics.Blit(Icon, Target);
				RenderTexture.active = Target;
				Readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
				Readback.Apply();

				// Weight by alpha so the transparent background doesn't count
				float R = 0f, G = 0f, B = 0f, A = 0f;
				foreach (Color Pixel in Readback.GetPixels())
				{
					R += Pixel.r * Pixel.a;
					G += Pixel.g * Pixel.a;
					B += Pixel.b * Pixel.a;
					A += Pixel.a;
				}

				if (A <= 0f)
					return false;

				Color Tint = def.graphicData?.color ?? Color.white;
				Average = new Color(R / A * Tint.r, G / A * Tint.g, B / A * Tint.b, 1f);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
			finally
			{
				RenderTexture.active = Previous;
				RenderTexture.ReleaseTemporary(Target);
				UnityEngine.Object.Destroy(Readback);
			}
		}

		// Mineables we will highlight. Update here to ignore.
		private static bool IsOreDef(ThingDef thingDef)
		{
			if ((thingDef.mineable == false) || (thingDef.building == null))
				return false;

			// Plain rock is not an ore. Don't use ThingDef.IsNonResourceNaturalRock for this: since 1.6 it returns false for
			// rock with mineablePreventNaturalRockOnSurface set, like Odyssey's vacstone, which then lit up whole asteroids.
			if ((thingDef.building.isNaturalRock == true) && (thingDef.building.isResourceRock == false))
				return false;

			if (thingDef.IsSmoothed || thingDef.IsBlueprint || thingDef.IsFrame)
				return false;

			// Ores drop something when mined. This skips boulders, VGE's sealant goop and VEE's deep snow.
			ThingDef Resource = thingDef.building.mineableThing;
			if (Resource == null)
				return false;

			// Walls that drop rock chunks are terrain, not ore, e.g. the ice and sand walls from Biomes! Core.
			if ((Resource.thingCategories != null) && Resource.thingCategories.Contains(ThingCategoryDefOf.StoneChunks))
				return false;

			if ((thingDef.defName.ToLower().StartsWith("smooth") == true) || (thingDef.defName.ToLower().StartsWith("raised") == true) || (thingDef.defName == "CollapsedRocks"))
				return false;

			return true;
		}

		private static bool IsOre(ThingDef def) => (def.index < m_IsOreDef.Length) && m_IsOreDef[def.index];

		private static bool IsSpecial(ThingDef def) => (def.index < m_IsSpecialDef.Length) && m_IsSpecialDef[def.index];

		// Forces a refresh on the next draw, e.g. after the mod settings changed.
		public void MarkStale()
		{
			m_Stale = true;
		}

		// Called every frame for the current map. Decides whether the grid needs to be rescanned.
		private void CheckForRefresh()
		{
			int TicksGame = Find.TickManager.TicksGame;

			// Update because ores were just turned on, or this map was just switched to
			if (m_WasShown == false)
			{
				m_WasShown = true;
				m_Stale = true;
				m_LastScannerCheckTick = -1;
			}

			// Update due to change in active scanner state
			if ((m_LastScannerCheckTick < 0) || (TicksGame - m_LastScannerCheckTick >= ScannerCheckIntervalTicks))
			{
				m_LastScannerCheckTick = TicksGame;

				bool ActiveScanner = this.m_Map.deepResourceGrid.AnyActiveDeepScannersOnMap();
				if (m_ActiveScanner != ActiveScanner)
				{
					m_ActiveScanner = ActiveScanner;
					m_Stale = true;
				}
			}

			// Update due to ticks passed
			if ((m_LastRefreshTick < 0) || (TicksGame - m_LastRefreshTick >= RefreshIntervalTicks))
				m_Stale = true;

			if (m_Stale == true)
				RefreshOreOverlayData();
		}

		// This recalculates the data used to produce the overlay.
		private void RefreshOreOverlayData()
		{
			m_Stale = false;
			m_LastRefreshTick = Find.TickManager.TicksGame;

			bool ShowSpecial = OreOverlayModOptions.m_ShowSteam;
			bool ActiveScanner = m_ActiveScanner;
			ThingGrid Grid = m_Map.thingGrid;
			FogGrid Fog = m_Map.fogGrid;
			bool Changed = false;

			// Now search every tile
			int NumCells = m_Map.cellIndices.NumGridCells;
			for (int i = 0; i < NumCells; i++)
			{
				ThingDef CellDef = null;
				Color CellColor = m_DefaultOreColor;
				bool CellIsSpecial = false;

				List<Thing> Things = Grid.ThingsListAtFast(i);
				for (int t = 0; t < Things.Count; t++)
				{
					ThingDef Def = Things[t].def;

					if (IsSpecial(Def))
					{
						CellDef = Def;
						CellColor = m_DefColor[Def.index];
						CellIsSpecial = true;
					}
					else if ((CellDef == null) && IsOre(Def))
					{
						CellDef = Def;
						CellColor = m_DefColor[Def.index];
					}
				}

				bool Visible = (CellDef != null) && ((ShowSpecial == true) || (CellIsSpecial == false)) && ((ActiveScanner == true) || (Fog.IsFogged(i) == false));

				// Only rebuild the overlay mesh if something actually changed
				if ((Visible != m_CellVisible[i]) || ((Visible == true) && (CellColor != m_ColorMap[i])))
					Changed = true;

				this.m_CellDef[i] = CellDef;
				this.m_ColorMap[i] = CellColor;
				this.m_CellVisible[i] = Visible;
			}

			if (Changed == true)
				m_Drawer.SetDirty();
        }

		static Color CreateColor(int r, int g, int b)
		{
			float Red = (float)r / 255f;
			float Green = (float)g / 255f;
			float Blue = (float)b / 255f;

			return (new Color(Red, Green, Blue));
		}
		#endregion
		#region Ore Drawing
		// This function draws the ore overlay, and is called from the same place existing overlays are called from both paused and unpaused.
		public void OreGridDraw()
		{
			if (m_ShowOres == true)
			{
				CheckForRefresh();

				this.MarkForDraw();
			}
			else
			{
				m_WasShown = false;
			}

			m_Drawer.CellBoolDrawerUpdate();

			// Check for draw debug so we dont do it twice.
			if ((DebugViewSettings.drawDeepResources == false) && (m_ShowDeepOres == true))
                this.m_Map.deepResourceGrid.MarkForDraw();
        }

        public void MarkForDraw()
        {
            if (this.map != Find.CurrentMap)
                return;

            this.m_Drawer.MarkForDraw();
        }

		// Only the current map is drawn and refreshed. Make sure a map gets a fresh scan when it is switched back to.
		public override void MapComponentTick()
		{
			if (this.map != Find.CurrentMap)
				m_WasShown = false;
		}

		// This is called during the overlay drawing for each cell to get the state.
		public bool GetCellBool(int index)
        {
			return (index < m_CellVisible.Length) && m_CellVisible[index];
        }
        
		// This is called during the overlay drawing to colour a cell if flagged above.
        public Color GetCellExtraColor(int index)
        {
			if (index >= m_ColorMap.Length)
				return m_DefaultOreColor;

			return m_ColorMap[index];
        }
		#endregion
		#region Mouseovers
		// Handle mouse-over
		public override void MapComponentOnGUI()
        {
            // Dont loose other hover-overs
            base.MapComponentOnGUI();
            bool DrawHoverover = true;

			// Handle the mouse-over for deep ore
            if (m_ShowDeepOres == true)
            {
                Thing singleSelectedThing = Find.Selector.SingleSelectedThing;
                if (singleSelectedThing != null)
                {
                    CompDeepScanner comp1 = singleSelectedThing.TryGetComp<CompDeepScanner>();
                    CompDeepDrill comp2 = singleSelectedThing.TryGetComp<CompDeepDrill>();

                    if ((comp1 != null) || (comp2 != null))
                        DrawHoverover = false;
                }

				/* Even if scanner is offline, you know where the deposit is by descovering it.
                if (DrawHoverover == true)
                {
                    if (this.m_Map.deepResourceGrid.AnyActiveDeepScannersOnMap() == false)
                        DrawHoverover = false;
                }
				*/

                if (DrawHoverover == true)
                    this.RenderDeepDrillDeposit();
            }
            
			// Handle the mouse over for cliff ores.
            if ((OreOverlayModOptions.m_ShowSeams == true) && (m_ShowOres == true))
            {
                IntVec3 MapCell = UI.MouseCell();
                if (!MapCell.InBounds(this.m_Map))
					return;

				int index = m_Map.cellIndices.CellToIndex(MapCell);
				ThingDef CellDef = m_CellDef[index];

				// Hide if steam
				if ((CellDef == null) || IsSpecial(CellDef))
					return;

				if ((m_ActiveScanner == false) && (MapCell.Fogged(this.m_Map) == true))
					return;

				RenderCliffDeposit(CellDef);
            }
        }

		// Draw the cliff ore mouseover
        private void RenderCliffDeposit(ThingDef TargetThing)
        {
            IntVec3 MapCell = UI.MouseCell();
            if (!MapCell.InBounds(this.m_Map))
                return;
            
			ThingDef thingDef = TargetThing.building?.mineableThing;

            if (thingDef == null)
                return;

            Vector2 uiPosition = MapCell.ToVector3().MapToUIPosition();
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            float num2 = (float)(((double)UI.CurUICellSize() - 27.0) / 2.0);
            Rect rect = new Rect(uiPosition.x + num2, uiPosition.y - UI.CurUICellSize() + num2, 27f, 27f);



            Widgets.ThingIcon(rect, thingDef);
            Widgets.Label(new Rect(rect.xMax + 4f, rect.y, 999f, 29f), NamedArgumentUtility.Named(thingDef, "RESOURCE").arg.ToString());
            Text.Anchor = TextAnchor.UpperLeft;
        }

		// Draw the deep ore mouseover
        private void RenderDeepDrillDeposit()
        {
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(this.m_Map))
                return;
            ThingDef thingDef = this.m_Map.deepResourceGrid.ThingDefAt(c);
            if (thingDef == null)
                return;
            int num1 = this.m_Map.deepResourceGrid.CountAt(c);
            if (num1 <= 0)
                return;
            Vector2 uiPosition = c.ToVector3().MapToUIPosition();
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            float num2 = (float)(((double)UI.CurUICellSize() - 27.0) / 2.0);
            Rect rect = new Rect(uiPosition.x + num2, uiPosition.y - UI.CurUICellSize() + num2, 27f, 27f);
            Widgets.ThingIcon(rect, thingDef);
            Widgets.Label(new Rect(rect.xMax + 4f, rect.y, 999f, 29f), "DeepResourceRemaining".Translate(NamedArgumentUtility.Named(thingDef, "RESOURCE"), num1.Named("COUNT")));
            Text.Anchor = TextAnchor.UpperLeft;
        }
		#endregion
	}
}
