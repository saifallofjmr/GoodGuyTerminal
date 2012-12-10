using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Runtime.InteropServices;

using NotMissing.Logging;

using Db4objects.Db4o;
using Db4objects.Db4o.Config;
using Db4objects.Db4o.TA;

using FluorineFx;
using FluorineFx.AMF3;
using FluorineFx.IO;
using FluorineFx.Messaging.Messages;
using FluorineFx.Messaging.Rtmp.Event;

using LoLNotes.Gui;
using LoLNotes.Gui.Controls;
using LoLNotes.Messages.Account;
using LoLNotes.Messages.Champion;
using LoLNotes.Messages.Commands;
using LoLNotes.Messages.GameLobby;
using LoLNotes.Messages.GameLobby.Participants;
using LoLNotes.Messages.GameStats;
using LoLNotes.Messages.Readers;
using LoLNotes.Messages.Statistics;
using LoLNotes.Messages.Summoner;
using LoLNotes.Properties;
using LoLNotes.Proxy;
using LoLNotes.Storage;
using LoLNotes.Util;

using Awesomium;
using Awesomium.Core;
using Awesomium.Windows.Forms;

namespace GG_TerminalOverlay
{
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width
        {
            get 
            {
                return Right - Left;
            }
        }

        public int Height
        {
            get
            {
                return Bottom - Top;
            }
        }

        public override string ToString()
        {
            return (String.Format("({0}, {1}) ({2}, {3}) Height={4}; Width={5}", Left, Top, Right, Bottom, Height, Width));
        }
    }

    struct WindowPlacement
    {
       public uint length;
       public uint flags;
       public uint showCmd;
       public Point minPosition;
       public Point maxPosition;
       public Rect normalPosition;
 
       public override string ToString()
       {
          return(String.Format("min, max, normal:{0}, {1}, {2}", minPosition, maxPosition, normalPosition));
       }
    }

    public partial class Form1 : Form
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32")]
        static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement wp);

        //AutoItX3Lib.AutoItX3Class au3 = new AutoItX3Lib.AutoItX3Class(); 

        IntPtr LoLClientHandle;
        KeyboardHook Hook = new KeyboardHook();
        bool IsPulledDown = true;

        // LoLNote's stuff
        Dictionary<LeagueRegion, CertificateHolder> Certificates;

        //RtmpsProxyHost is an rtmps proxy that allows us to intercept/send packets.
        RtmpsProxyHost Connection;
        //MessageReader converts ASObjects to Message Objects making them easier to work with.
        MessageReader Reader;
        //CertificateInstaller installs the ssl certificates which are required for RtmpsProxyHost to work.
        CertificateInstaller Installer;
        //ProcessInjector forces lol to connect to RtmpsProxyHost instead of their servers.
        ProcessInjector Injector;
        GameDTO CurrentGame;
        List<ChampionDTO> Champions;
        SummonerData SelfSummoner;

        bool Connected = false;
        bool DocumentDoneLoading = false;
        string Status = "Waiting...";

        Queue<Action> PendingEvals = new Queue<Action>();

        string TerminalUrl = "http://gggamers.org/terminal/";


        public Form1()
        {
            InitializeComponent();

            

            this.Hide();
        }

        void Hook_KeyPressed(object sender, KeyPressedEventArgs e)
        {
            if (this.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
            }
        }

        void repositionOverlay_Tick(object sender, EventArgs e)
        {
            // We aren't even being displayed
            if (!IsPulledDown)
                return;

            if (LoLClientHandle != IntPtr.Zero)
            {
                Rect rect;
                GetWindowRect(LoLClientHandle, out rect);

                if (rect.Left == -32000 && LoLClientHandle != IntPtr.Zero)
                {
                    // the game is minimized
                    this.WindowState = FormWindowState.Minimized;
                }
                else if(rect.Right == 0 && rect.Left == 0)
                {
                    // the game got closed, find the next one!
                    LoLClientHandle = IntPtr.Zero;
                }
                else
                {
                    this.WindowState = FormWindowState.Normal;

                    WindowPlacement wp = new WindowPlacement();
                    GetWindowPlacement(LoLClientHandle, ref wp);

                    this.Location = new Point(rect.Left, rect.Top);

                    this.Size = new Size(rect.Width, rect.Height);
                }
            }
            else
            {
                // We aren't logged in and such yet!  Where's the window?!
                LoLClientHandle = FindWindow(null, "PVP.net client");

                if (LoLClientHandle != IntPtr.Zero)
                {
                    //this.FormBorderStyle = FormBorderStyle.None;

                    Rect rect;
                    GetWindowRect(LoLClientHandle, out rect);

                    SetWindowPos(LoLClientHandle, (IntPtr)(-2), 0, 0, 0, 0, 0x01 | 0x02);
                }
                else
                {
                    //this.FormBorderStyle = FormBorderStyle.FixedDialog;
                }
            }


            //
            EvalPendingJS();


            SetStatus(Status);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            /*
             * Logging
            DefaultListener listener = new DefaultListener(Levels.All,
                    (LogHandler)delegate(Levels level, object log)
                    {
                        System.IO.File.AppendAllText("output.log", level.ToString() + " > " + log.ToString() + Environment.NewLine);
                        //WriteLine(log.ToString());
                    });

            StaticLogger.Register(listener);
             */

            /*
             * Intercepting + listening to LoL
             */
            Certificates = new Dictionary<LeagueRegion, CertificateHolder>
			                    {
				                    {LeagueRegion.NA, new CertificateHolder("prod.na1.lol.riotgames.com", Resources.prod_na1_lol_riotgames_com)},
				                    {LeagueRegion.EUW, new CertificateHolder("prod.eu.lol.riotgames.com", Resources.prod_eu_lol_riotgames_com)},
				                    {LeagueRegion.EUN, new CertificateHolder("prod.eun1.lol.riotgames.com", Resources.prod_eun1_lol_riotgames_com)},
				                    {LeagueRegion.GARENA, new CertificateHolder("prod.lol.garenanow.com", Resources.prod_lol_garenanow_com)},
 			                    };

            var cert = Certificates[LeagueRegion.NA];

            Injector = new ProcessInjector("lolclient");
            Connection = new RtmpsProxyHost(2099, cert.Domain, 2099, cert.Certificate);
            Reader = new MessageReader(Connection);

            Connection.Connected += Connection_Connected;

            Injector.Injected += Injector_Injected;

            Reader.ObjectRead += Reader_ObjectRead;

            Installer = new CertificateInstaller(Certificates.Select(c => c.Value.Certificate).ToArray());
            //Install the ssl certificates
            if (!Installer.IsInstalled)
            {
                Installer.Install();
            }

            try
            {
                Injector.Start();
                Connection.Start();
            }
            catch (Exception e23)
            {
                int done = 1;
            }

            /*
             * Jack in
             */
            webControl1.LoadURL(TerminalUrl);
            webControl1.LoadCompleted += new EventHandler(webControl1_LoadCompleted);

            //Awesomium.Core.
            //WebSession session = WebCore.CreateWebSession("C:\\SessionDataPath", WebPreferences.Default);


            /* 
             * Setup the form
             */
            System.Windows.Forms.Timer repositionOverlay = new System.Windows.Forms.Timer();
            repositionOverlay.Interval = 100;
            repositionOverlay.Tick += new EventHandler(repositionOverlay_Tick);
            repositionOverlay.Start();

            Hook.KeyPressed += new EventHandler<KeyPressedEventArgs>(Hook_KeyPressed);

            Hook.RegisterHotKey(GG_TerminalOverlay.ModifierKeys.None, Keys.F1);
        }

        /// <summary>
        /// Hook into so we can switch between moving/not moving whether LoL is open or not
        /// </summary>
        /// <param name="message"></param>
        protected override void WndProc(ref Message message)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MOVE = 0xF010;

            /*
            switch(message.Msg)
            {
                case WM_SYSCOMMAND:
                    int command = message.WParam.ToInt32() & 0xfff0;

                    // Allow them to move this window around while LoL isn't open
                    if (command == SC_MOVE && LoLClientHandle != IntPtr.Zero)
                        return;
                    break;
            }
             */

            base.WndProc(ref message);
        }

        

        void webControl1_LoadCompleted(object sender, EventArgs e)
        {
            DocumentDoneLoading = true;

            Status = "Waiting...";
        }

        void Injector_Injected(object sender, EventArgs e)
        {
            Status = "Intercepting";
        }

        void Connection_Connected(object sender, EventArgs e)
        {
            RtmpsProxyHost rtmps = sender as RtmpsProxyHost;



            if (rtmps.IsConnected)
            {
                Connected = true;
                Status = "Connected!";
            }
            else
            {
                Connected = false;
                Status = "Disconnected...";
            }
        }

        void Reader_ObjectRead(object obj)
        {
            if (obj is GameDTO)
            {
                CurrentGame = (GameDTO)obj;

                string team = "";

                foreach (PlayerParticipant p in CurrentGame.TeamOne)
                {
                    team += p.Name + ", ";
                }

                if (team != "")
                    team = team.Remove(team.Length - 3);


                UpdateTeam(team, 1);



                // Keep it DRY ya'll
                team = "";

                foreach (PlayerParticipant p in CurrentGame.TeamTwo)
                {
                    team += p.Name + ", ";
                }

                if (team != "")
                    team = team.Remove(team.Length - 3);

                UpdateTeam(team, 2);

                UpdateMap(CurrentGame.MapId);
            }
            else if (obj is EndOfGameStats)
            {
                return;
            }
            else if (obj is List<ChampionDTO>)
            {
                Champions = (List<ChampionDTO>)obj;
            }
            else if (obj is LoginDataPacket)
            {
                SelfSummoner = ((LoginDataPacket)obj).AllSummonerData.Summoner;
            }
        }

        void UpdateMap(int mapId)
        {
            PendJS(
            (Action)delegate()
            {
                Awesomium.Core.JSValue[] param = new Awesomium.Core.JSValue[1];
                param[0] = new Awesomium.Core.JSValue(mapId);

                webControl1.CallJavascriptFunction("GgTerminal", "updateMap", param);
            });
        }

        void UpdateTeam(string s, int team)
        {
            PendJS(
            (Action)delegate()
            {
                Awesomium.Core.JSValue[] param = new Awesomium.Core.JSValue[2];
                param[0] = new Awesomium.Core.JSValue(s);
                param[1] = new Awesomium.Core.JSValue(team);

                webControl1.CallJavascriptFunction("GgTerminal", "updateTeam", param);
            });
        }

        void SetStatus(string s)
        {
            PendJS(
                (Action)delegate()
                {
                    webControl1.CallJavascriptFunction("", "setStatus", new Awesomium.Core.JSValue(s));
                });
        }

        void PendJS(Action action)
        {
            PendingEvals.Enqueue(action);
        }

        void EvalPendingJS()
        {
            if (DocumentDoneLoading && !webControl1.IsLoadingPage)
            {
                while (PendingEvals.Count > 0)
                {
                    Action a = PendingEvals.Dequeue();

                    if(a != null)
                        a.Invoke();
                }
            }
        }

        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            webControl1.LoadURL(TerminalUrl);
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            if (webControl1.IsCrashed)
                return;

            webControl1.GoBack();
        }


        private void toolStripButton4_Click(object sender, EventArgs e)
        {
            if (webControl1.IsCrashed)
                return;

            webControl1.Reload();
        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            if (webControl1.IsCrashed)
                return;

            webControl1.GoForward();
        }
    }
}
