using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StarDio {
	internal sealed class ModEntry : Mod {
		class SDPlayer {
			public bool InSkullCavern;
			public bool CanPause;
			public bool Ticked;

			public SDPlayer()
			{
				this.InSkullCavern = false;
				this.CanPause = false;
				this.Ticked = false;
			}
		}

		class SDMessage {
			public byte InSkullCavern;
			public byte CanPause;

			public SDMessage()
			{
				this.InSkullCavern = 0;
				this.CanPause = 0;
			}

			public SDMessage(bool inSkullCavern, bool canPause)
			{
				if (inSkullCavern) {
					this.InSkullCavern = 1;
				} else {
					this.InSkullCavern = 0;
				}

				if (canPause) {
					this.CanPause = 1;
				} else {
					this.CanPause = 0;
				}
			}
		}

		class AllData {
			public Dictionary<long, SDPlayer> Players;
			public bool InSkullCavern;
			public bool CanPause;
			public bool TickStarted;

			public AllData()
			{
				this.Players = new Dictionary<long, SDPlayer>();
				this.Reset();
			}

			public void Reset()
			{
				this.InSkullCavern = false;
				this.CanPause = false;
				this.TickStarted = false;

				foreach (KeyValuePair<long, SDPlayer> plr in this.Players) {
					plr.Value.InSkullCavern = false;
					plr.Value.CanPause = false;
					plr.Value.Ticked = false;
				}
			}

			public void RemovePlayer(long playerId)
			{
				if (this.Players.ContainsKey(playerId)) {
					this.Players.Remove(playerId);
				}
			}

			public void AddPlayer(long playerId)
			{
				this.RemovePlayer(playerId);
				this.Players.Add(playerId, new SDPlayer());
			}

			public SDPlayer GetPlayer(long playerId)
			{
				if (!this.Players.ContainsKey(playerId)) {
					return null;
				}
				return this.Players[playerId];
			}
		}

		private static readonly PerScreen<int> OldGameTimeInterval = new PerScreen<int>();

		private static AllData All;

		private readonly PerScreen<SDPlayer> LastState = new PerScreen<SDPlayer>(createNewState: () => null);
		private readonly PerScreen<SDPlayer> CurrState = new PerScreen<SDPlayer>(createNewState: () => null);

		public override void Entry(IModHelper helper)
		{
			ModEntry.All = null;

			var harmony = new Harmony(this.ModManifest.UniqueID);

			harmony.Patch(
				original: AccessTools.Method(
					typeof(StardewValley.Game1),
					nameof(StardewValley.Game1.UpdateGameClock)
				),
				prefix: new HarmonyMethod(
					typeof(ModEntry).GetMethod(
						"UpdateGameClock_Prefix", 
						BindingFlags.NonPublic | BindingFlags.Static
					)
				),
				postfix: new HarmonyMethod(
					typeof(ModEntry).GetMethod(
						"UpdateGameClock_Postfix", 
						BindingFlags.NonPublic | BindingFlags.Static
					)
				)
			);

			helper.Events.GameLoop.SaveLoaded += HandleSaveLoaded;
			helper.Events.GameLoop.ReturnedToTitle += HandleReturnedToTitle;
			helper.Events.GameLoop.UpdateTicking += HandleUpdateTicking;

			helper.Events.Multiplayer.PeerConnected += HandlePeerConnected;
			helper.Events.Multiplayer.PeerDisconnected += HandlePeerDisconnected;
			helper.Events.Multiplayer.ModMessageReceived += HandleModMessageReceived;
		}

		private void LogToChat(String s)
		{
			//Game1.chatBox.addInfoMessage(s);
		}

		private void HandleSaveLoaded(object sender, SaveLoadedEventArgs evt)
		{
			this.LastState.Value = new SDPlayer();
			this.CurrState.Value = new SDPlayer();

			AllData temp = ModEntry.All;
			ModEntry.All = null;

			if (!Context.IsMultiplayer) {
				return;
			}
			if (!Context.IsOnHostComputer) {
				return;
			}

			ModEntry.All = temp;
			if (ModEntry.All == null) {
				ModEntry.All = new AllData();
			}
		}

		private void HandleReturnedToTitle(object sender, ReturnedToTitleEventArgs evt)
		{
			this.LastState.ResetAllScreens();
			this.CurrState.ResetAllScreens();

			ModEntry.All = null;
		}

		private void UpdateAll()
		{
			foreach (KeyValuePair<int, SDPlayer> plr in this.CurrState.GetActiveValues()) {
				plr.Value.Ticked = false;
			}
			ModEntry.All.CanPause = true;
			ModEntry.All.InSkullCavern = true;
			foreach (KeyValuePair<int, SDPlayer> plr in this.LastState.GetActiveValues()) {
				if (!plr.Value.CanPause) {
					ModEntry.All.CanPause = false;
				}
				if (!plr.Value.InSkullCavern) {
					ModEntry.All.InSkullCavern = false;
				}
			}
			foreach (KeyValuePair<long, SDPlayer> plr in ModEntry.All.Players) {
				if (!plr.Value.CanPause) {
					ModEntry.All.CanPause = false;
				}
				if (!plr.Value.InSkullCavern) {
					ModEntry.All.InSkullCavern = false;
				}
			}
		}

		private void HandleUpdateTicking(object sender, UpdateTickingEventArgs evt)
		{
			if (!Context.IsWorldReady || !Context.IsMultiplayer) {
				return;
			}

			if (this.LastState.Value == null || this.CurrState.Value == null) {
				return;
			}

			if (ModEntry.All != null && !ModEntry.All.TickStarted) {
				UpdateAll();
				ModEntry.All.TickStarted = true;
			}

			if (Game1.shouldTimePass(true) && (Game1.currentMinigame == null)) {
				this.CurrState.Value.CanPause = false;
			} else {
				this.CurrState.Value.CanPause = true;
			}

			if (Game1.player.currentLocation is StardewValley.Locations.MineShaft &&
			    (Game1.player.currentLocation as StardewValley.Locations.MineShaft).getMineArea() == 121) {
				this.CurrState.Value.InSkullCavern = true;
			} else {
				this.CurrState.Value.InSkullCavern = false;
			}

			bool sendUpdate = false;
			if (this.LastState.Value.CanPause != this.CurrState.Value.CanPause ||
			    this.LastState.Value.InSkullCavern != this.CurrState.Value.InSkullCavern) {
				this.LastState.Value.CanPause = this.CurrState.Value.CanPause;
				this.LastState.Value.InSkullCavern = this.CurrState.Value.InSkullCavern;
				sendUpdate = true;
			}

			this.CurrState.Value.Ticked = true;

			if (ModEntry.All == null || !Context.IsOnHostComputer) {
				if (sendUpdate) {
					SDMessage msg = new SDMessage(
						this.CurrState.Value.InSkullCavern,
						this.CurrState.Value.CanPause
					);
					this.Helper.Multiplayer.SendMessage(msg, "SDMessage", modIDs: new[] { this.ModManifest.UniqueID });

					this.LogToChat($"[StarDio] SENT: ({this.CurrState.Value.InSkullCavern}, {this.CurrState.Value.CanPause})");
				}
				return;
			}

			foreach (KeyValuePair<int, SDPlayer> plr in this.CurrState.GetActiveValues()) {
				if (!plr.Value.Ticked) {
					return;
				}
			}

			ModEntry.All.TickStarted = false;
		}

		private void HandlePeerConnected(object sender, PeerConnectedEventArgs evt)
		{
			AllData temp = ModEntry.All;
			ModEntry.All = null;

			if (evt.Peer.IsHost) {
				return;
			}
			if (!Context.IsOnHostComputer) {
				return;
			}

			ModEntry.All = temp;
			if (ModEntry.All == null) {
				ModEntry.All = new AllData();
			}

			if (!Context.IsMainPlayer) {
				return;
			}

			ModEntry.All.AddPlayer(evt.Peer.PlayerID);
		}

		private void HandlePeerDisconnected(object sender, PeerDisconnectedEventArgs evt)
		{
			if (ModEntry.All == null) {
				return;
			}
			if (!Context.IsMultiplayer) {
				ModEntry.All = null;
				return;
			}
			if (!Context.IsMainPlayer) {
				return;
			}

			ModEntry.All.RemovePlayer(evt.Peer.PlayerID);
		}

		private void HandleModMessageReceived(object sender, ModMessageReceivedEventArgs evt)
		{
			if (!Context.IsMainPlayer) {
				return;
			}
			if (ModEntry.All == null) {
				return;
			}
			if (evt.Type != "SDMessage" || evt.FromModID != this.ModManifest.UniqueID) {
				return;
			}
			SDPlayer plr = ModEntry.All.GetPlayer(evt.FromPlayerID);
			if (plr == null) {
				return;
			}

			SDMessage msg = evt.ReadAs<SDMessage>();
			if (msg.InSkullCavern == 1) {
				plr.InSkullCavern = true;
			} else {
				plr.InSkullCavern = false;
			}

			if (msg.CanPause == 1) {
				plr.CanPause = true;
			} else {
				plr.CanPause = false;
			}

			this.LogToChat($"[StarDio] RECV: ({plr.InSkullCavern}, {plr.CanPause})");
		}

		private static void UpdateGameClock_Prefix(GameTime time)
		{
			if (ModEntry.All == null || Game1.IsClient) {
				return;
			}
			if (ModEntry.All.Players.Count == 0) {
				return;
			}
			ModEntry.OldGameTimeInterval.Value = Game1.gameTimeInterval;
			if (ModEntry.All.InSkullCavern) {
				Game1.gameTimeInterval -= 2000;
			}
			if (!ModEntry.All.CanPause) {
				return;
			}
			if (Game1.shouldTimePass()) {
				Game1.gameTimeInterval -= time.ElapsedGameTime.Milliseconds;
			}
		}

		private static void UpdateGameClock_Postfix(GameTime time)
		{
			if (ModEntry.All == null || Game1.IsClient) {
				return;
			}
			if (ModEntry.All.Players.Count == 0) {
				return;
			}
			if (ModEntry.OldGameTimeInterval.Value > 2000 &&
			    Game1.gameTimeInterval == 0) {
				return;
			}
			Game1.gameTimeInterval = ModEntry.OldGameTimeInterval.Value;
			if (ModEntry.All.CanPause) {
				return;
			}
			if (Game1.shouldTimePass()) {
				Game1.gameTimeInterval += time.ElapsedGameTime.Milliseconds;
			}
		}

	}
}