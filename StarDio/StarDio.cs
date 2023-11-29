using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace StarDio {
	internal sealed class StarDio : Mod {
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

		class ClientUpdate {
			public byte InSkullCavern;
			public byte CanPause;

			public ClientUpdate()
			{
				this.InSkullCavern = 0;
				this.CanPause = 0;
			}

			public ClientUpdate(bool inSkullCavern, bool canPause)
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

		class ServerUpdate {
			public byte Paused;

			public ServerUpdate()
			{
				this.Paused = 0;
			}

			public ServerUpdate(bool paused)
			{
				if (paused) {
					this.Paused = 1;
				} else {
					this.Paused = 0;
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

		private static bool Paused;

		public override void Entry(IModHelper helper)
		{
			StarDio.All = null;
			StarDio.Paused = false;

			var harmony = new Harmony(this.ModManifest.UniqueID);

			harmony.Patch(
				original: AccessTools.Method(
					typeof(StardewValley.Game1),
					nameof(StardewValley.Game1.UpdateGameClock)
				),
				prefix: new HarmonyMethod(
					typeof(StarDio).GetMethod(
						"UpdateGameClock_Prefix", 
						BindingFlags.NonPublic | BindingFlags.Static
					)
				),
				postfix: new HarmonyMethod(
					typeof(StarDio).GetMethod(
						"UpdateGameClock_Postfix", 
						BindingFlags.NonPublic | BindingFlags.Static
					)
				)
			);

			harmony.Patch(
				original: AccessTools.Method(
					typeof(StardewValley.Menus.DayTimeMoneyBox),
					nameof(StardewValley.Menus.DayTimeMoneyBox.draw),
					new Type[] {
						typeof(Microsoft.Xna.Framework.Graphics.SpriteBatch)
					}
				),
				transpiler: new HarmonyMethod(
					typeof(StarDio).GetMethod(
						"DayTimeMoneyBox_Draw_Transpiler",
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
			StarDio.Paused = false;

			AllData temp = StarDio.All;
			StarDio.All = null;

			if (!Context.IsMultiplayer) {
				return;
			}
			if (!Context.IsOnHostComputer) {
				return;
			}

			StarDio.All = temp;
			if (StarDio.All == null) {
				StarDio.All = new AllData();
			}
		}

		private void HandleReturnedToTitle(object sender, ReturnedToTitleEventArgs evt)
		{
			this.LastState.ResetAllScreens();
			this.CurrState.ResetAllScreens();

			StarDio.All = null;
		}

		private void SendClientUpdate()
		{
			ClientUpdate msg = new ClientUpdate(
				this.CurrState.Value.InSkullCavern,
				this.CurrState.Value.CanPause
			);
			this.Helper.Multiplayer.SendMessage(msg, "CU", modIDs: new[] { this.ModManifest.UniqueID });
			this.LogToChat($"[StarDio] SENT: ({this.CurrState.Value.InSkullCavern}, {this.CurrState.Value.CanPause})");
		}

		private void SendServerUpdate()
		{
			ServerUpdate msg = new ServerUpdate(StarDio.Paused);
			this.Helper.Multiplayer.SendMessage(msg, "SU", modIDs: new[] { this.ModManifest.UniqueID });
			this.LogToChat($"[StarDio] SENT: ({StarDio.Paused})");
		}

		private void UpdateAll()
		{
			foreach (KeyValuePair<int, SDPlayer> plr in this.CurrState.GetActiveValues()) {
				plr.Value.Ticked = false;
			}
			bool couldPause = StarDio.All.CanPause;
			bool wasInSkullCavern = StarDio.All.InSkullCavern;
			StarDio.All.CanPause = true;
			foreach (KeyValuePair<int, SDPlayer> plr in this.LastState.GetActiveValues()) {
				if (!plr.Value.CanPause) {
					StarDio.All.CanPause = false;
				}
				if (!plr.Value.InSkullCavern) {
					StarDio.All.InSkullCavern = false;
				}
			}
			foreach (KeyValuePair<long, SDPlayer> plr in StarDio.All.Players) {
				if (!plr.Value.CanPause) {
					StarDio.All.CanPause = false;
				}
				if (!plr.Value.InSkullCavern) {
					StarDio.All.InSkullCavern = false;
				}
			}
			StarDio.Paused = StarDio.All.CanPause;
			if (couldPause != StarDio.All.CanPause) {
				this.SendServerUpdate();
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

			if (StarDio.All != null && !StarDio.All.TickStarted) {
				UpdateAll();
				StarDio.All.TickStarted = true;
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

			if (StarDio.All == null || !Context.IsOnHostComputer) {
				if (sendUpdate) {
					this.SendClientUpdate();
				}
				return;
			}

			foreach (KeyValuePair<int, SDPlayer> plr in this.CurrState.GetActiveValues()) {
				if (!plr.Value.Ticked) {
					return;
				}
			}

			StarDio.All.TickStarted = false;
		}

		private void HandlePeerConnected(object sender, PeerConnectedEventArgs evt)
		{
			AllData temp = StarDio.All;
			StarDio.All = null;

			if (evt.Peer.IsHost) {
				return;
			}
			if (!Context.IsOnHostComputer) {
				return;
			}

			StarDio.All = temp;
			if (StarDio.All == null) {
				StarDio.All = new AllData();
			}

			if (!Context.IsMainPlayer) {
				return;
			}

			StarDio.All.AddPlayer(evt.Peer.PlayerID);

			this.SendServerUpdate();
		}

		private void HandlePeerDisconnected(object sender, PeerDisconnectedEventArgs evt)
		{
			if (StarDio.All == null) {
				return;
			}
			if (!Context.IsMultiplayer) {
				StarDio.All = null;
				return;
			}
			if (!Context.IsMainPlayer) {
				return;
			}

			StarDio.All.RemovePlayer(evt.Peer.PlayerID);
		}

		private void ClientHandleMessage(object sender, ModMessageReceivedEventArgs evt)
		{
			if (evt.Type != "SU") {
				return;
			}

			ServerUpdate msg = evt.ReadAs<ServerUpdate>();
			if (msg.Paused == 1) {
				StarDio.Paused = true;
			} else {
				StarDio.Paused = false;
			}

			this.LogToChat($"[StarDio] RECV: ({StarDio.Paused})");
		}

		private void ServerHandleMessage(object sender, ModMessageReceivedEventArgs evt)
		{
			if (evt.Type != "CU") {
				return;
			}

			SDPlayer plr = StarDio.All.GetPlayer(evt.FromPlayerID);
			if (plr == null) {
				return;
			}

			ClientUpdate msg = evt.ReadAs<ClientUpdate>();
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

		private void HandleModMessageReceived(object sender, ModMessageReceivedEventArgs evt)
		{
			if (evt.FromModID != this.ModManifest.UniqueID) {
				return;
			}

			if (!Context.IsMainPlayer || StarDio.All == null) {
				ClientHandleMessage(sender, evt);
			} else {
				ServerHandleMessage(sender, evt);
			}
		}

		private static void UpdateGameClock_Prefix(GameTime time)
		{
			if (StarDio.All == null || Game1.IsClient) {
				return;
			}
			if (StarDio.All.Players.Count == 0) {
				return;
			}
			StarDio.OldGameTimeInterval.Value = Game1.gameTimeInterval;
			if (StarDio.All.InSkullCavern) {
				Game1.gameTimeInterval -= 2000;
			}
			if (!StarDio.All.CanPause) {
				return;
			}
			if (Game1.shouldTimePass()) {
				Game1.gameTimeInterval -= time.ElapsedGameTime.Milliseconds;
			}
		}

		private static bool ShouldTimePass(bool dummy)
		{
			return !StarDio.Paused;
		}

		private static void UpdateGameClock_Postfix(GameTime time)
		{
			if (StarDio.All == null || Game1.IsClient) {
				return;
			}
			if (StarDio.All.Players.Count == 0) {
				return;
			}
			if (StarDio.OldGameTimeInterval.Value > 2000 &&
			    Game1.gameTimeInterval == 0) {
				return;
			}
			Game1.gameTimeInterval = StarDio.OldGameTimeInterval.Value;
			if (StarDio.All.CanPause) {
				return;
			}
			if (Game1.shouldTimePass()) {
				Game1.gameTimeInterval += time.ElapsedGameTime.Milliseconds;
			}
		}

		private static IEnumerable<CodeInstruction> DayTimeMoneyBox_Draw_Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instr in instructions) {
				if (instr.opcode != OpCodes.Call) {
					continue;
				}
				if (!(instr.operand is MethodInfo)) {
					continue;
				}
				MethodInfo m = instr.operand as MethodInfo;
				if (!m.DeclaringType.Equals(typeof(Game1))) {
					continue;
				}
				if (m.Name != "shouldTimePass") {
					continue;
				}
				instr.operand = typeof(StarDio).GetMethod(
					"ShouldTimePass",
					BindingFlags.NonPublic | BindingFlags.Static
				);
				break;
			}
			return instructions;
		}

	}
}