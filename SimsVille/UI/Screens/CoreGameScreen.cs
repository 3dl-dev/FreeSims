/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Panels;
using FSO.Client.UI.Model;
using Microsoft.Xna.Framework;
using FSO.Client.Utils;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework;
using FSO.Client.Network;
using FSO.LotView;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Utils;
using FSO.SimAntics.Primitives;
using TSO.HIT;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.NetPlay.Model.Commands;
using System.IO;
using FSO.SimAntics.NetPlay;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Panels.WorldUI;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.Common;
using SimsVille.UI.Model;
using FSO.Client.Rendering.City;
using tso.world.Model;
using FSO.Vitaboy;
using FSO.SimAntics.Model.TSOPlatform;
using Microsoft.Xna.Framework.Graphics;
using FSO.Files.Formats.IFF;

namespace FSO.Client.UI.Screens
{
    public class CoreGameScreen : FSO.Client.UI.Framework.GameScreen
    {
        public UIUCP ucp;
        public UIGizmo gizmo;
        public UIInbox Inbox;
        public UIGameTitle Title;
        private UIButton SaveHouseButton;
        private UIButton VMDebug;
        private UIButton CreateChar;
        private string[] CityMusic;
        private String city, lotName;

        private string[] CharacterInfos;      
        public List<XmlCharacter> Characters;

        private bool Connecting, Permissions;
        private UILoginProgress ConnectingDialog;
        private Queue<SimConnectStateChange> StateChanges;
        private UIMouseEventRef MouseHitAreaEventRef = null;
        private Neighborhood CityRenderer; //city view

        private bool _AutoLotLoadAttempted = false;
        private int _AutoLotLoadCountdown = 60; //wait ~1s of frames for scene to settle

        // --- Thunk pattern for agent-initiated lot loads (reeims-e8e) ---
        //
        // External agents call VMNetLoadLotCmd.Execute from the VM tick thread
        // (inside VMIPCDriver.Tick). Directly invoking LoadLotByXmlName from
        // that thread would mutate the world that the VM is actively ticking,
        // so we queue a request via the static thunk below. CoreGameScreen.Update
        // (on the UI thread) consumes the request at the start of each tick.
        //
        // volatile write + lock on _lotLoadLock is sufficient: we don't need a
        // full queue because multiple in-flight load requests within a single
        // frame coalesce to "the most recent one" — an agent that issues two
        // load-lots in quick succession is expressing intent to end up on the
        // second one, not both.
        private static readonly object _lotLoadLock = new object();
        private static volatile string _pendingLotLoad = null;

        /// <summary>
        /// Request a lot load from any thread (typically the VM tick thread inside
        /// VMIPCDriver). The request is consumed by CoreGameScreen.Update on the
        /// UI thread. Safe to call when no CoreGameScreen instance exists — the
        /// request just stays pending until one is created.
        /// </summary>
        public static void RequestLotLoad(string xmlName)
        {
            if (string.IsNullOrEmpty(xmlName)) return;
            lock (_lotLoadLock)
            {
                _pendingLotLoad = xmlName;
            }
        }

        /// <summary>
        /// Internal: consume a pending lot load request (test-visible).
        /// Returns the requested xml name, or null if none pending. Clears the slot.
        /// </summary>
        internal static string ConsumePendingLotLoad()
        {
            lock (_lotLoadLock)
            {
                var v = _pendingLotLoad;
                _pendingLotLoad = null;
                return v;
            }
        }

        public UILotControl LotController; //world, lotcontrol and vm will be null if we aren't in a lot.
        private LotView.World World;
        public FSO.SimAntics.VM vm;
        public bool InLot
        {
            get
            {
                return (vm != null);
            }
        }

        private int m_ZoomLevel;
        public int ZoomLevel
        {
            get
            {
                return m_ZoomLevel;
            }
            set
            {
                value = Math.Max(1, Math.Min(5, value));
                if (value < 4)
                {
                    if (vm == null) ZoomLevel = 4; //call this again but set minimum cityrenderer view
                    else
                    {
                        Title.SetTitle(LotController.GetLotTitle());
                        if (m_ZoomLevel > 3)
                        {
                            HITVM.Get().PlaySoundEvent(UIMusic.None);
                            gizmo.Visible = false;
                            CityRenderer.Visible = false;
                            LotController.Visible = true;
                            World.Visible = true;
                            ucp.SetMode(UIUCP.UCPMode.LotMode);
                        }
                        m_ZoomLevel = value;
                        vm.Context.World.State.Zoom = (WorldZoom)(4 - ZoomLevel); //near is 3 for some reason... will probably revise
                    }
                }
                else //cityrenderer! we'll need to recreate this if it doesn't exist...
                {
                    
                        Title.SetTitle(city);                  

                        if (m_ZoomLevel < 4)
                        {   
                            //coming from lot view... snap zoom % to 0 or 1
                            CityRenderer.m_ZoomProgress = (value == 4) ? 1 : 0;
                            //PlayBackgroundMusic(CityMusic); //play the city music as well
                            CityRenderer.Visible = true;

                            HITVM.Get().PlaySoundEvent(UIMusic.Map); //play the city music as well
                            gizmo.Visible = true;
                            if (World != null)
                            {
                                World.Visible = false;
                                LotController.Visible = false;
                            }
                            ucp.SetMode(UIUCP.UCPMode.CityMode);
                        }
                        m_ZoomLevel = value;

                        CityRenderer.m_Zoomed = (value == 4);

                }
                ucp.UpdateZoomButton();
            }
        } //in future, merge LotDebugScreen and CoreGameScreen so that we can store the City+Lot combo information and controls in there.

        private int _Rotation = 0;
        public int Rotation
        {
            get
            {
                return _Rotation;
            }
            set
            {
                _Rotation = value;
                if (World != null)
                {
                    switch (_Rotation)
                    {
                        case 0:
                            World.State.Rotation = WorldRotation.TopLeft; break;
                        case 1:
                            World.State.Rotation = WorldRotation.TopRight; break;
                        case 2:
                            World.State.Rotation = WorldRotation.BottomRight; break;
                        case 3:
                            World.State.Rotation = WorldRotation.BottomLeft; break;
                    }
                }
            }
        }

        public sbyte Level
        {
            get
            {
                if (World == null) return 1;
                else return World.State.Level;
            }
            set
            {
                if (World != null)
                {
                    World.State.Level = value;
                }
            }
        }

        public sbyte Stories
        {
            get
            {
                if (World == null) return 2;
                return World.Stories;
            }
        }

        public CoreGameScreen() : base()
        {
            /** City Scene **/
            ListenForMouse(new Rectangle(0, 0, ScreenWidth, ScreenHeight), new UIMouseEvent(MouseHandler));


            city = "Queen Margaret's";
            if (PlayerAccount.CurrentlyActiveSim != null)
                city = PlayerAccount.CurrentlyActiveSim.ResidingCity.Name;


            StateChanges = new Queue<SimConnectStateChange>();

            /**
            * Music
            */
            CityMusic = new string[]{
                GlobalSettings.Default.StartupPath + "\\music\\modes\\map\\tsobuild1.mp3",
                GlobalSettings.Default.StartupPath + "\\music\\modes\\map\\tsobuild3.mp3",
                GlobalSettings.Default.StartupPath + "\\music\\modes\\map\\tsomap2_v2.mp3",
                GlobalSettings.Default.StartupPath + "\\music\\modes\\map\\tsomap3.mp3",
                GlobalSettings.Default.StartupPath + "\\music\\modes\\map\\tsomap4_v1.mp3"
            };
            HITVM.Get().PlaySoundEvent(UIMusic.Map);

            VMDebug = new UIButton()
            {
                Caption = "Simantics",
                Y = 45,
                Width = 100,
                X = GlobalSettings.Default.GraphicsWidth - 110
            };
            VMDebug.OnButtonClick += new ButtonClickDelegate(VMDebug_OnButtonClick);
            this.Add(VMDebug);
            //InitializeMouse();

            
            CharacterInfos = new string[9];


            SaveHouseButton = new UIButton()
            {
                Caption = "Save House",
                Y = 10,
                Width = 100,
                X = GlobalSettings.Default.GraphicsWidth - 110
            };
            SaveHouseButton.OnButtonClick += new ButtonClickDelegate(SaveHouseButton_OnButtonClick);
            this.Add(SaveHouseButton);
            SaveHouseButton.Visible = false;

            CreateChar = new UIButton()
            {
                Caption = "Create Sim",
                Y = 10,
                Width = 100,
                X = GlobalSettings.Default.GraphicsWidth - 110
            };
            CreateChar.OnButtonClick += new ButtonClickDelegate(CreateChar_OnButtonClick);
            CreateChar.Visible = true;
            this.Add(CreateChar);

            ucp = new UIUCP(this);
            ucp.Y = ScreenHeight - 210;
            ucp.SetInLot(false);
            ucp.UpdateZoomButton();
            ucp.MoneyText.Caption = PlayerAccount.Money.ToString();
            this.Add(ucp);

            gizmo = new UIGizmo();
            gizmo.X = ScreenWidth - 500;
            gizmo.Y = ScreenHeight - 300;
            this.Add(gizmo);

            Title = new UIGameTitle();
            Title.SetTitle(city);
            this.Add(Title);

            

            //OpenInbox();

            this.Add(GameFacade.MessageController);
            GameFacade.MessageController.OnSendLetter += new LetterSendDelegate(MessageController_OnSendLetter);
            GameFacade.MessageController.OnSendMessage += new MessageSendDelegate(MessageController_OnSendMessage);

            NetworkFacade.Controller.OnNewTimeOfDay += new OnNewTimeOfDayDelegate(Controller_OnNewTimeOfDay);
            NetworkFacade.Controller.OnPlayerJoined += new OnPlayerJoinedDelegate(Controller_OnPlayerJoined);


            CityRenderer = new Neighborhood(GameFacade.Game.GraphicsDevice);
            CityRenderer.LoadContent();

            CityRenderer.Initialize(GameFacade.HousesDataRetriever);


            CityRenderer.SetTimeOfDay(0.5);

            GameFacade.Scenes.Add(CityRenderer);

            ZoomLevel = 4; //Nhood view.

            
        }

        private void InitializeMouse()
        {
            /** City Scene **/
            UIContainer mouseHitArea = new UIContainer();
            MouseHitAreaEventRef = mouseHitArea.ListenForMouse(new Rectangle(0, 0, ScreenWidth, ScreenHeight), new UIMouseEvent(MouseHandler));
            AddAt(0, mouseHitArea);
        }

        public override void GameResized()
        {
            base.GameResized();
            Title.SetTitle(Title.Label.Caption);
            ucp.Y = ScreenHeight - 210;
            gizmo.X = ScreenWidth - 430;
            gizmo.Y = ScreenHeight - 230;
            //MessageTray.X = ScreenWidth - 70;

            //if (World != null)
               // World.GameResized();
            var oldPanel = ucp.CurrentPanel;
            ucp.SetPanel(-1);
            ucp.SetPanel(oldPanel);
            if (MouseHitAreaEventRef != null)
            {
                MouseHitAreaEventRef.Region = new Rectangle(0, 0, ScreenWidth, ScreenHeight);
            }

        }

        #region Network handlers

        private void Controller_OnNewTimeOfDay(DateTime TimeOfDay)
        {
            if (TimeOfDay.Hour <= 12)
                ucp.TimeText.Caption = TimeOfDay.Hour + ":" + TimeOfDay.Minute + "am";
            else ucp.TimeText.Caption = TimeOfDay.Hour + ":" + TimeOfDay.Minute + "pm";

            double time = TimeOfDay.Hour / 24.0 + TimeOfDay.Minute / (1440.0) + TimeOfDay.Second / (86400.0);
        }

        private void Controller_OnPlayerJoined(LotTileEntry TileEntry)
        {
            
        }

        #endregion

        private void MessageController_OnSendMessage(string message, string GUID)
        {
            //TODO: Implement special packet for message (as opposed to letter)?
            //Don't send empty strings!!
            Network.UIPacketSenders.SendLetter(Network.NetworkFacade.Client, message, "Empty", GUID);
        }

        /// <summary>
        /// Message was sent by player to another player.
        /// </summary>
        /// <param name="message">Message to send.</param>
        /// <param name="subject">Subject of message.</param>
        /// <param name="destinationUser">GUID of destination user.</param>
        private void MessageController_OnSendLetter(string message, string subject, string destinationUser)
        {
            Network.UIPacketSenders.SendLetter(Network.NetworkFacade.Client, message, subject, destinationUser);
        }

        public override void Update(FSO.Common.Rendering.Framework.Model.UpdateState state)
        {
            GameFacade.Game.IsFixedTimeStep = (vm == null || vm.Ready);

            base.Update(state);

            // AGENT LOT LOAD (reeims-e8e): consume any pending load request from
            // VMNetLoadLotCmd.Execute. Runs before vm.Update so we tear down the
            // VM *before* the same frame's tick would otherwise mutate it.
            var pendingXml = ConsumePendingLotLoad();
            if (pendingXml != null)
            {
                try
                {
                    LoadLotByXmlName(pendingXml);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[loadlot] EXCEPTION for " + pendingXml + ": " + ex);
                }
            }

            //AUTO-LOAD: headless baseline harness — pick first character + first house and enter lot view.
            if (!_AutoLotLoadAttempted && vm == null)
            {
                if (_AutoLotLoadCountdown-- <= 0)
                {
                    _AutoLotLoadAttempted = true;
                    try
                    {
                        var charDir = Path.Combine(FSOEnvironment.ContentDir ?? "Content", "Characters");
                        var houseDir = Path.Combine(FSOEnvironment.ContentDir ?? "Content", "Houses");
                        if (!Directory.Exists(charDir)) charDir = "Content/Characters/";
                        if (!Directory.Exists(houseDir)) houseDir = "Content/Houses/";
                        var charFiles = Directory.GetFiles(charDir, "*.xml");
                        var houseFiles = Directory.GetFiles(houseDir, "*.xml");
                        Console.Error.WriteLine("[autoload] charFiles=" + charFiles.Length + " houseFiles=" + houseFiles.Length);
                        if (charFiles.Length > 0 && houseFiles.Length > 0)
                        {
                            gizmo.SelectedCharInfo = XmlCharacter.Parse(charFiles[0]);
                            var houseXml = houseFiles.FirstOrDefault(f => f.EndsWith("house1.xml")) ?? houseFiles[0];
                            Console.Error.WriteLine("[autoload] char=" + charFiles[0] + " house=" + houseXml);
                            InitTestLot(houseXml, Path.GetFileName(houseXml), true, false);
                            Console.Error.WriteLine("[autoload] InitTestLot returned. vm=" + (vm != null) + " World=" + (World != null) + " ZoomLevel=" + ZoomLevel);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[autoload] EXCEPTION: " + ex);
                    }
                }
            }


            lock (StateChanges)
            {
                while (StateChanges.Count > 0)
                {
                    var e = StateChanges.Dequeue();
                    ClientStateChangeProcess(e.State, e.Progress);
                }
            }

            if (vm != null) vm.Update();

            if (!Visible)
                CityRenderer.Visible = false;

            if (ZoomLevel < 4)
            {

                CreateChar.Visible = false;
                SaveHouseButton.Visible = true;

            }
            else if (ZoomLevel >= 4)
            {

                CreateChar.Visible = true;
                SaveHouseButton.Visible = false;

            }
        }

        public void CleanupLastWorld()
        {
            if (ZoomLevel < 4) ZoomLevel = 5;
            vm.Context.Ambience.Kill();
            foreach (var ent in vm.Entities) { //stop object sounds
                var threads = ent.SoundThreads;
                for (int i = 0; i < threads.Count; i++)
                {
                    threads[i].Sound.RemoveOwner(ent.ObjectID);
                }
                threads.Clear();
            }
            vm.CloseNet(VMCloseNetReason.LeaveLot);
            
            GameFacade.Scenes.Remove(World);
            this.Remove(LotController);
            ucp.SetPanel(-1);
            ucp.SetInLot(false);
        }

        public void ClientStateChange(int state, float progress)
        {
            lock (StateChanges) StateChanges.Enqueue(new SimConnectStateChange(state, progress));
        }

        public void ClientStateChangeProcess(int state, float progress)
        {
            //TODO: queue these up and try and sift through them in an update loop to avoid UI issues. (on main thread)
            if (state == 4) //disconnected
            {
                var reason = (VMCloseNetReason)progress;
                if (reason == VMCloseNetReason.Unspecified)
                {
                    var alert = UIScreen.ShowAlert(new UIAlertOptions
                    {
                        Title = GameFacade.Strings.GetString("222", "3"),
                        Message = GameFacade.Strings.GetString("222", "2", new string[] { "0" }),
                    }, true);

                    if (Connecting)
                    {
                        UIScreen.RemoveDialog(ConnectingDialog);
                        ConnectingDialog = null;
                        Connecting = false;
                    }

                    alert.ButtonMap[UIAlertButtonType.OK].OnButtonClick += DisconnectedOKClick;
                } else
                {
                    DisconnectedOKClick(null);
                }
            }

            if (ConnectingDialog == null) return;
            switch (state)
            {
                case 1:
                    ConnectingDialog.ProgressCaption = GameFacade.Strings.GetString("211", "26");
                    ConnectingDialog.Progress = 25f;
                    break;
                case 2:
                    ConnectingDialog.ProgressCaption = GameFacade.Strings.GetString("211", "27");
                    ConnectingDialog.Progress = 100f*(0.5f+progress*0.5f);
                    break;
                case 3:
                    UIScreen.RemoveDialog(ConnectingDialog);
                    ConnectingDialog = null;
                    Connecting = false;
                    ZoomLevel = 1;
                    ucp.SetInLot(true);
                    break;
            }
        }

        private void DisconnectedOKClick(UIElement button)
        {
            if (vm != null) CleanupLastWorld();
            Connecting = false;
        }

        /// <summary>
        /// Loads a lot by XML filename (reeims-e8e). Resolves <paramref name="xmlName"/>
        /// relative to the Content/Houses directory, cancels all in-flight VM actions,
        /// tears down the current VM/World/LotController, and loads the new blueprint
        /// via InitTestLot. Must be called on the UI thread.
        ///
        /// Disables the auto-load countdown so subsequent idle ticks don't re-fire
        /// the baseline auto-load path on top of the agent-requested lot.
        /// </summary>
        public void LoadLotByXmlName(string xmlName)
        {
            if (string.IsNullOrEmpty(xmlName))
            {
                Console.Error.WriteLine("[loadlot] empty xmlName, ignoring");
                return;
            }

            // Resolve path. Accept both bare "house2.xml" and absolute paths.
            string housePath;
            if (Path.IsPathRooted(xmlName) && File.Exists(xmlName))
            {
                housePath = xmlName;
            }
            else
            {
                var houseDir = Path.Combine(FSOEnvironment.ContentDir ?? "Content", "Houses");
                if (!Directory.Exists(houseDir)) houseDir = "Content/Houses/";
                housePath = Path.Combine(houseDir, xmlName);
            }

            if (!File.Exists(housePath))
            {
                Console.Error.WriteLine("[loadlot] house xml not found: " + housePath);
                return;
            }

            Console.Error.WriteLine("[loadlot] request: " + xmlName + " -> " + housePath);

            // Cancel all queued/active actions on every avatar BEFORE teardown.
            // This prevents crashes from mid-action destruction during Reset().
            if (vm != null)
            {
                try
                {
                    foreach (var ent in vm.Entities.ToList())
                    {
                        var th = ent?.Thread;
                        if (th == null) continue;
                        try
                        {
                            // Snapshot queue to a list before iterating — CancelAction
                            // may mutate the queue during iteration.
                            var queueSnapshot = th.Queue?.ToList();
                            if (queueSnapshot != null)
                            {
                                foreach (var action in queueSnapshot)
                                {
                                    if (action != null)
                                        th.CancelAction(action.UID);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("[loadlot] cancel-all error on entity " + ent.ObjectID + ": " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[loadlot] cancel-all outer error: " + ex);
                }
            }

            // Disable auto-load countdown so it doesn't re-fire on the new lot.
            _AutoLotLoadAttempted = true;

            // Pick a reasonable default character if one isn't already selected.
            // On first agent-triggered load after a fresh boot (auto-load skipped),
            // gizmo.SelectedCharInfo may be null.
            if (gizmo.SelectedCharInfo == null)
            {
                try
                {
                    var charDir = Path.Combine(FSOEnvironment.ContentDir ?? "Content", "Characters");
                    if (!Directory.Exists(charDir)) charDir = "Content/Characters/";
                    var charFiles = Directory.GetFiles(charDir, "*.xml");
                    if (charFiles.Length > 0)
                        gizmo.SelectedCharInfo = XmlCharacter.Parse(charFiles[0]);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[loadlot] default char selection failed: " + ex.Message);
                }
            }

            if (gizmo.SelectedCharInfo == null)
            {
                Console.Error.WriteLine("[loadlot] no character available, aborting");
                return;
            }

            // InitTestLot already calls CleanupLastWorld when vm != null, so we
            // don't need an explicit teardown here — just delegate. The result:
            // old VM closes, new VM is constructed, new blueprint loads, new
            // simJoin command places the agent's Sim.
            InitTestLot(housePath, Path.GetFileName(housePath), true, false);

            Console.Error.WriteLine("[loadlot] done. vm=" + (vm != null) + " world=" + (World != null));
        }

        public void InitTestLot(string path, string name, bool host, bool TS1)
        {
            if (Connecting) return;

            lotName = name.Split('.')[0];



            Characters = new List<XmlCharacter>();

            SaveHouseButton.Visible = true;
            CreateChar.Visible = false;

            if (vm != null) CleanupLastWorld();

            World = new LotView.World(GameFacade.Game.GraphicsDevice);
            GameFacade.Scenes.Add(World);

            

            vm = new VM(new VMContext(World), new UIHeadlineRendererProvider());
            vm.Init();
            vm.LotName = (path == null) ? "localhost" : path.Split('/').LastOrDefault(); //quick hack just so we can remember where we are
            

            var DirectoryInfo = new DirectoryInfo(Path.Combine(FSOEnvironment.UserDir, "Characters/"));

            for (int i = 0; i <= DirectoryInfo.GetFiles().Count() - 1; i++)
            {


                var file = DirectoryInfo.GetFiles()[i];
                CharacterInfos[i] = Path.GetFileNameWithoutExtension(file.FullName);

                if (CharacterInfos[i] != null && CharacterInfos[i] != gizmo.SelectedCharInfo.Name)
                {
                    Characters.Add(XmlCharacter.Parse(file.FullName));


                }

            }


            VMNetDriver driver;
            VMIPCDriver ipcDriver = null;
            if (Environment.GetEnvironmentVariable("FREESIMS_IPC") == "1")
            {
                ipcDriver = new VMIPCDriver();
                driver = ipcDriver;
            }
            else if (host)
            {
                driver = new VMLocalDriver();
            }
            else
            {
                Connecting = true;
                ConnectingDialog = new UILoginProgress();

                ConnectingDialog.Caption = GameFacade.Strings.GetString("211", "1");
                ConnectingDialog.ProgressCaption = GameFacade.Strings.GetString("211", "24");
                //this.Add(ConnectingDialog);

                UIScreen.ShowDialog(ConnectingDialog, true);

                driver = new VMLocalDriver();
            }


            vm.VM_SetDriver(driver);

            // Wire dialog events to the IPC driver after the VM driver is set.
            ipcDriver?.SubscribeToVM(vm);



            IffFile HouseFile = new IffFile();

            if (host)
            {
                //check: do we have an fsov to try loading from?

                
                string filename = Path.GetFileName(path);
                try
                {
                    using (var file = new BinaryReader(File.OpenRead(Path.Combine(FSOEnvironment.UserDir, "Houses/") + filename.Substring(0, filename.Length - 4) + ".fsov")))
                    {
                        var marshal = new SimAntics.Marshals.VMMarshal();
                        marshal.Deserialize(file);
                        vm.Load(marshal);
                        vm.Reset();
                    }
                }
                catch (Exception)
                {
                    short jobLevel = -1;

                    //quick hack to find the job level from the chosen blueprint
                    //the final server will know this from the fact that it wants to create a job lot in the first place...

                    try
                    {
                        if (filename.StartsWith("nightclub") || filename.StartsWith("restaurant") || filename.StartsWith("robotfactory"))
                            jobLevel = Convert.ToInt16(filename.Substring(filename.Length - 9, 2));
                    }
                    catch (Exception) { }

                    if (TS1)
                        HouseFile = new IffFile(path);

                    vm.SendCommand(new VMBlueprintRestoreCmd
                    {
                        JobLevel = -1,
                        XMLData = File.ReadAllBytes(path),
                        Characters = Characters,
                        HouseFile = HouseFile,
                        TS1 = TS1

                    });
                }
            }

            
            //Check the clients loaded;
            List<VMAvatar> Clients = new List<VMAvatar>();

            foreach (VMEntity entity in vm.Entities)
                if (entity is VMAvatar && entity.PersistID > 0)
                    Clients.Add((VMAvatar)entity);


            if (Clients.Count == 0)
                Permissions = true;

            uint simID = (uint)(new Random()).Next();
            vm.MyUID = simID;


            //Load clients data
            AppearanceType type;

            VMWorldActivator activator = new VMWorldActivator(vm, World);

            var headPurchasable = Content.Content.Get().AvatarPurchasables.Get(Convert.ToUInt64(gizmo.SelectedCharInfo.Head, 16), false);
            var bodyPurchasable = Content.Content.Get().AvatarPurchasables.Get(Convert.ToUInt64(gizmo.SelectedCharInfo.Body, 16), false);
            var HeadID = headPurchasable != null ? headPurchasable.OutfitID :
                Convert.ToUInt64(gizmo.SelectedCharInfo.Head, 16);
            var BodyID = bodyPurchasable != null ? bodyPurchasable.OutfitID :
                Convert.ToUInt64(gizmo.SelectedCharInfo.Body, 16);


            Enum.TryParse(gizmo.SelectedCharInfo.Appearance, out type);
            bool Male = (gizmo.SelectedCharInfo.Gender == "male") ? true : false;

            vm.SendCommand(new VMNetSimJoinCmd
            {
                ActorUID = simID,
                HeadID = HeadID,
                BodyID = BodyID,
                SkinTone = (byte)type,
                Gender = Male,
                Name = gizmo.SelectedCharInfo.Name,
                Permissions = (Permissions == true) ?
                VMTSOAvatarPermissions.Owner : VMTSOAvatarPermissions.Visitor
            });


            LotController = new UILotControl(vm, World);
            this.AddAt(0, LotController);

            vm.Context.Clock.Hours = 10;
         
            if (m_ZoomLevel > 3)
            {
                World.Visible = false;
                LotController.Visible = false;
            }
        

            if (host)
            {
                ZoomLevel = 1;
                ucp.SetInLot(true);



            } else
            {
                ZoomLevel = Math.Max(ZoomLevel, 4);
            }

            vm.OnFullRefresh += VMRefreshed;
            vm.OnChatEvent += Vm_OnChatEvent;
            vm.OnEODMessage += LotController.EODs.OnEODMessage;

        }

        private void Vm_OnChatEvent(SimAntics.NetPlay.Model.VMChatEvent evt)
        {
            if (ZoomLevel < 4)
            {
                Title.SetTitle(LotController.GetLotTitle());
            }
        }

        private void VMRefreshed()
        {
            if (vm == null) return;
            LotController.ActiveEntity = null;
            LotController.RefreshCut();
        }

        private void VMDebug_OnButtonClick(UIElement button)
        {
            // WinForms-based Simantics debug panel is excluded from the Linux build.
            // The button remains in the UI but is a no-op for now.
        }

        private void CreateChar_OnButtonClick(UIElement button)
        {

            if (CityRenderer != null)
            {
                Visible = false;
                CityRenderer.Visible = false;

                GameFacade.Controller.ShowPersonCreation(new ProtocolAbstractionLibraryD.CityInfo(false));

            }

        }

        private void SaveHouseButton_OnButtonClick(UIElement button)
        {
            int houses = 0;


            DirectoryInfo HousesDir;

            if (vm == null) return;

            if (!Directory.Exists(Path.Combine(FSOEnvironment.UserDir, "Houses/")))
            {
                HousesDir = Directory.CreateDirectory(Path.Combine(FSOEnvironment.UserDir, "Houses/"));
            }

            HousesDir = new DirectoryInfo(Path.Combine(FSOEnvironment.UserDir, "Houses/"));

            foreach (FileInfo file in HousesDir.GetFiles())
                if (file.Extension == ".xml")
                    houses += 1;

            var exporter = new VMWorldExporter();

            if (lotName == "empty_lot")
                lotName = "house0" + houses;

            string housePath = Path.Combine(FSOEnvironment.UserDir, "Houses/", lotName);

            exporter.SaveHouse(vm, housePath + ".xml");
            var marshal = vm.Save();

           // if (marshal != null)
            //using (var output = new FileStream(Path.Combine(FSOEnvironment.UserDir, "Houses/" + lotName + ".fsov"), FileMode.Create))
              //  {
                //    marshal.SerializeInto(new BinaryWriter(output));
                //}

            Texture2D lotThumb = World.GetLotThumb(GameFacade.GraphicsDevice);

            if (lotThumb != null)
                using (var output = File.Open(housePath + ".png", FileMode.OpenOrCreate))
                {
                    lotThumb.SaveAsPng(output, lotThumb.Width, lotThumb.Height);
                }
         

            //if (vm.GlobalLink != null) ((VMTSOGlobalLinkStub)vm.GlobalLink).Database.Save();
        }

        public void CloseInbox()
        {
            this.Remove(Inbox);
            Inbox = null;
        }

        public void OpenInbox()
        {
            if (Inbox == null)
            {
                Inbox = new UIInbox();
                this.Add(Inbox);
                Inbox.X = GlobalSettings.Default.GraphicsWidth / 2 - 332;
                Inbox.Y = GlobalSettings.Default.GraphicsHeight / 2 - 184;
            }
            //todo, on already visible move to front
        }

        private void MouseHandler(UIMouseEventType type, UpdateState state)
        {
            
            //if (CityRenderer != null) CityRenderer.UIMouseEvent(type.ToString()); //all the city renderer needs are events telling it if the mouse is over it or not.
            //if the mouse is over it, the city renderer will handle the rest.
        }
    }

    public class SimConnectStateChange
    {
        public int State;
        public float Progress;
        public SimConnectStateChange(int state, float progress)
        {
            State = state; Progress = progress;
        }
    }
}
