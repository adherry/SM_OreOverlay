using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace OreOverlay
{
	[StaticConstructorOnStartup]
	class OreOverlayModOptionHandler : Mod
	{
		//public static OreOverlayModOptions m_Settings;

		public OreOverlayModOptionHandler(ModContentPack content) : base(content)
		{
			base.GetSettings<OreOverlayModOptions>();
		}

		public void Save()
		{
			try
			{
				LoadedModManager.GetMod<OreOverlayModOptionHandler>().GetSettings<OreOverlayModOptions>().Write();
			}
			catch (Exception)
			{

			}
		}

		public override string SettingsCategory()
		{
			string CatString = "Ore Overlay";

			try
			{
				CatString = "OreOverlay.OptionCat".Translate();
			}
			catch (Exception)
			{
				CatString = "Ore Overlay";
			}

			return CatString;
		}

		public override void DoSettingsWindowContents(Rect inRect)
		{
			OreOverlayModOptions.DoSettingsWindowContents(inRect);
		}


		public override void WriteSettings()
		{
			base.WriteSettings();
			//m_Settings.Init();

			try
			{
				if (Find.CurrentMap != null)
				{
					OreOverlayGrid Overlay = Find.CurrentMap.GetComponent<OreOverlayGrid>();

					if (Overlay != null)
					{
						Overlay.MarkStale(); // Settings changed, force a refresh.
					}
				}
			}
			catch (Exception)
			{

			}
		}
	}
}