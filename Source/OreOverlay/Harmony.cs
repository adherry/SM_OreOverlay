using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using UnityEngine;
using Verse;

namespace OreOverlay
{
    // Initialise
    [StaticConstructorOnStartup]
    class HarmonyPatches
    {
        public static readonly Texture2D m_HudIcon = null;
        public static readonly Texture2D m_DeepHudIcon = null;

        static HarmonyPatches()
        {
            try
            {
                m_HudIcon = ContentFinder<Texture2D>.Get("UI/Buttons/ShowOreOverlay");
                m_DeepHudIcon = ContentFinder<Texture2D>.Get("UI/Buttons/ShowDeepOverlay");
            }
            catch(Exception)
            {
                Log.Warning("Exception loading icons for Ore Overlay");
            }
            
            Harmony harmony = new Harmony("adherry.OreOverlayImproved");
            harmony.PatchAll();

            // Build the ore lookup here, on the main thread. Maps are loaded on a worker thread, where the icon colour readback would crash Unity.
            OreOverlayGrid.PrepareDefLookup();
        }
    }


	// Add GUI buttons
	[HarmonyPatch(typeof(PlaySettings), "DoPlaySettingsGlobalControls")]
    internal static class HarmoneyPatchPlaySettings
    {
        [HarmonyPostfix]
        private static void PostFix(WidgetRow row, bool worldView)
        {
            if (worldView)
                return;

            if (row == null)
                return;

            if ((HarmonyPatches.m_HudIcon == null) || (HarmonyPatches.m_DeepHudIcon == null))
                return;

            row.ToggleableIcon(ref OreOverlayGrid.m_ShowOres, HarmonyPatches.m_HudIcon, "OreOverlay.ShowOverlay".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            row.ToggleableIcon(ref OreOverlayGrid.m_ShowDeepOres, HarmonyPatches.m_DeepHudIcon, "OreOverlay.ShowDrillOverlay".Translate(), SoundDefOf.Mouseover_ButtonToggle);
        }
    }
	
	// Insert a refresher in the same location used by other overlays. We want to avoid calculations here but drawing here allows the overlay to show during game pause.
	[HarmonyPatch(typeof(MapInterface))]
	internal static class HarmoneyPatchUpdate
	{
		[HarmonyPostfix]
		[HarmonyPatch("MapInterfaceUpdate")]
		private static void Postfix()
		{
			// Same check as MapInterfaceUpdate itself: no map exists (e.g. gravship in flight), or the world map is shown.
			if ((Find.CurrentMap == null) || (WorldRendererUtility.DrawingMap == false))
				return;

			OreOverlayGrid Overlay = Find.CurrentMap.GetComponent<OreOverlayGrid>();

			if (Overlay != null)
			{
				Overlay.OreGridDraw(); // Draw!
			}
		}
	}
}
