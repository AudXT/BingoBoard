using EldenBingo.GameInterop;
using EldenBingo.Net;
using EldenBingo.Properties;
using EldenBingo.Rendering;
using EldenBingo.Settings;
using EldenBingo.Sfx;
using EldenBingo.UI;
using EldenBingoCommon;
using EldenBingoServer;
using Neto.Shared;
using SFML.System;
using System.Security.Principal;

namespace EldenBingo
{
    public partial class MainForm : Form
    {
        private readonly Client _client;
        private readonly GameProcessHandler _processHandler;
        private MapCoordinateProviderHandler? _mapCoordinateProviderHandler;
        private MapWindow? _mapWindow;
        private Thread? _mapWindowThread;
        private Server? _server = null;
        private string _lastRoom = string.Empty;
        private string _lastAdminPass = string.Empty;
        private SoundLibrary _sounds;
        private bool _autoReconnect;
        private static object _connectLock = new object();
        private bool _connecting = false;

        private const int WM_HOTKEY_MSG_ID = 0x0312;

        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

        public MainForm()
        {
            InitializeComponent();
            Icon = Resources.icon;
            _processHandler = new GameProcessHandler();
            _processHandler.StatusChanged += _processHandler_StatusChanged;
            _processHandler.CoordinatesChanged += _processHandler_CoordinatesChanged;
            _sounds = new SoundLibrary();

            if (Properties.Settings.Default.MainWindowSizeX > 0 && Properties.Settings.Default.MainWindowSizeY > 0)
            {
                Width = Properties.Settings.Default.MainWindowSizeX;
                Height = Properties.Settings.Default.MainWindowSizeY;
            }

            FormClosing += (o, e) =>
            {
                _autoReconnect = false;
                _processHandler.Dispose();
                _sounds.Dispose();
                _mapWindow?.DisposeDrawablesOnExit();
                _mapWindow?.Stop();
                _client?.Disconnect();
            };
            _client = new Client();
            addClientListeners(_client);

            listenToSettingsChanged();
            SizeChanged += mainForm_SizeChanged;
        }

        public static Font GetFontFromSettings(Font defaultFont, float size, float defaultSize = 12f)
        {
            var ffName = Properties.Settings.Default.BingoFont;
            var scale = Properties.Settings.Default.BingoFontSize / defaultSize;
            if (!string.IsNullOrWhiteSpace(ffName))
            {
                try
                {
                    Font? font;
                    var ff2 = new FontFamily(ffName);
                    font = new Font(ff2, size * scale, (FontStyle)Properties.Settings.Default.BingoFontStyle);
                    if (font.Name == ffName)
                        return font;
                }
                catch(ArgumentException)
                {
                    //Font was not found
                }
            }
            return defaultFont;
        }

        /// <summary>
        /// Checks if the user has called this application as administrator.
        /// </summary>
        /// <returns>True if application is running as administrator.</returns>
        private static bool IsAdministrator()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private async void _connectButton_Click(object sender, EventArgs e)
        {
            lock (_connectLock)
            {
                if (_connecting)
                {
                    _consoleControl.PrintToConsole("Already connecting...", Color.Red);
                    return;
                }
            }
            var form = new ConnectForm();
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                await connect(form.Address, form.Port);
            }
        }

        private async Task connect(string address, int port)
        {
            lock (_connectLock)
            {
                if (_connecting)
                    return;

                _connecting = true;
            }
            try
            {
                var connectRetries = 5;
                while (_connecting && connectRetries > 0)
                {
                    try
                    {
                        ConnectionResult connectResult = await initClientAsync(address, port);
                        if (connectResult == ConnectionResult.Connected)
                        {
                            if (_autoReconnect && !string.IsNullOrEmpty(_lastRoom))
                            {
                                await _client.JoinRoom(
                                _lastRoom,
                                _lastAdminPass,
                                Properties.Settings.Default.Nickname,
                                Properties.Settings.Default.Team);
                            }
                            //Set the flag to automatically reconnect. This will be set to false if a disconnect is triggered manually or by kick
                            _autoReconnect = true;
                            return; //Successfully connected, so we return immediately
                        }
                    }
                    catch
                    {
                        //Try again
                    }
                    --connectRetries;
                    await Task.Delay(2000);
                }
            }
            finally
            {
                _connecting = false;
            }
        }

        private async void _createLobbyButton_Click(object sender, EventArgs e)
        {
            if (_client?.IsConnected != true)
                return;

            var form = new CreateLobbyForm(_client, true);
            _ = _client.RequestRoomName();
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _lastRoom = form.RoomName.Trim();
                _lastAdminPass = form.AdminPassword;
                await _client.CreateRoom(
                    _lastRoom,
                    _lastAdminPass,
                    form.Nickname,
                    form.Team,
                    GameSettingsHelper.ReadFromSettings(Properties.Settings.Default));
            }
        }

        private async void _disconnectButton_Click(object sender, EventArgs e)
        {
            if (_client?.IsConnected != true && !_connecting)
                return;

            var res = MessageBox.Show(this, "Disconnect from server?", Application.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (res == DialogResult.Yes)
            {
                _connecting = false;
                _autoReconnect = false;
                if(_client?.IsConnected == true)
                    await _client.Disconnect();
                updateButtonAvailability();
            }
        }

        private async void _joinLobbyButton_Click(object sender, EventArgs e)
        {
            if (_client?.IsConnected != true)
                return;

            var form = new CreateLobbyForm(_client, false);
            form.RoomName = _lastRoom;
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _lastRoom = form.RoomName.Trim();
                _lastAdminPass = form.AdminPassword;
                await _client.JoinRoom(
                    _lastRoom,
                    form.AdminPassword,
                    form.Nickname,
                    form.Team);
            }
        }

        private async void _leaveRoomButton_Click(object sender, EventArgs e)
        {
            if (_client?.IsConnected != true)
                return;

            var res = MessageBox.Show(this, "Leave current lobby?", Application.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (res == DialogResult.Yes)
            {
                await _client.LeaveRoom();
                updateButtonAvailability();
            }
        }

        private void _openMapButton_Click(object sender, EventArgs e)
        {
            try
            {
                openMapWindow();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening map window: ${ex.Message}");
            }
        }

        private void _processHandler_CoordinatesChanged(object? sender, MapCoordinateEventArgs e)
        {
            if (_client?.IsConnected == true && _client.Room != null && _client?.LocalUser?.IsSpectator == false)
            {
                ClientCoordinates coords;
                if (e.Coordinates.HasValue)
                    coords = new ClientCoordinates(e.Coordinates.Value.X, e.Coordinates.Value.Y, e.Coordinates.Value.Angle, e.Coordinates.Value.IsUnderground);
                else
                    coords = new ClientCoordinates(0, 0, 0, false);

                _ = _client.SendPacketToServer(new Packet(coords));
            }
        }

        private void _processHandler_StatusChanged(object? sender, StatusEventArgs e)
        {
            BeginInvoke(new Action(() =>
            {
                _processMonitorStatusTextBox.Text = e.Status;
                _processMonitorStatusTextBox.BackColor = e.Color;
            }));
        }

        private void _settingsButton_Click(object sender, EventArgs e)
        {
            var settingsDialog = new SettingsDialog();
            var res = settingsDialog.ShowDialog(this);
            if (res == DialogResult.OK)
            {
            }
        }

        private async void _startGameButton_Click(object sender, EventArgs e)
        {
            try
            {
                await tryStartingGameWithoutEAC();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        private void addClientListeners(Client? client)
        {
            if (client == null)
                return;

            client.Connected += client_Connected;
            client.Disconnected += client_Disconnected;
            client.Kicked += client_Kicked;
            client.OnStatus += client_OnStatus;
            client.OnError += client_OnError;
            client.OnRoomChanged += client_RoomChanged;

            client.AddListener<ServerJoinRoomAccepted>(joinRoomAccepted);
            client.AddListener<ServerJoinRoomDenied>(joinRoomDenied);
            client.AddListener<ServerEntireBingoBoardUpdate>(gotBingoBoard);
            client.AddListener<ServerUserChecked>(userCheckedSquare);
        }

        private void client_Connected(object? sender, EventArgs e)
        {
            updateButtonAvailability();
        }

        private async void client_Disconnected(object? sender, StringEventArgs e)
        {
            Invoke(hideLobbyTab);
            updateButtonAvailability();
            BeginInvoke(() =>
            {
                _consoleControl.PrintToConsole(e.Message, Color.Red);
                _clientStatusTextBox.Text = _client.GetConnectionStatusString();
            });
            if (_autoReconnect && !string.IsNullOrWhiteSpace(Properties.Settings.Default.ServerAddress))
            {
                await connect(Properties.Settings.Default.ServerAddress, Properties.Settings.Default.Port);
            }
            _autoReconnect = false;
        }

        private void client_Kicked(object? sender, EventArgs e)
        {
            _autoReconnect = false; //So we don't reconnect automatically after kick
        }

        private void joinRoomAccepted(ClientModel? _, ServerJoinRoomAccepted joinRoomAcceptedArgs)
        {
            updateButtonAvailability();
        }

        private void joinRoomDenied(ClientModel? _, ServerJoinRoomDenied joinRoomDeniedArgs)
        {
            updateButtonAvailability();
        }

        private void gotBingoBoard(ClientModel? _, ServerEntireBingoBoardUpdate bingoBoardArgs)
        {
            if (Properties.Settings.Default.ShowClassesOnMap && _mapWindow != null && _client.Room != null &&
                //If we got available classes in preparation phase, or within 20 seconds of the match starting -> Show the available classes
                (_client.Room.Match.MatchStatus == MatchStatus.Preparation || 
                _client.Room.Match.MatchStatus == MatchStatus.Running && _client.Room.Match.MatchMilliseconds < 20000)
                )
                _mapWindow.ShowAvailableClasses(bingoBoardArgs.AvailableClasses);
        }

        private void userCheckedSquare(ClientModel? _, ServerUserChecked userCheckedSquareArgs)
        {
            if (Properties.Settings.Default.PlaySounds && userCheckedSquareArgs.TeamChecked.HasValue)
            {
                _sounds.PlaySound(SoundType.SquareClaimed);
            }
        }

        private void showLobbyTab()
        {
            void update()
            {
                if (!tabControl1.TabPages.Contains(_lobbyPage))
                    tabControl1.TabPages.Add(_lobbyPage);
                tabControl1.SelectedIndex = 1;
            }
            if (InvokeRequired)
            {
                BeginInvoke(update);
                return;
            }
            update();
        }

        private void hideLobbyTab()
        {
            void update()
            {
                tabControl1.TabPages.Remove(_lobbyPage);
                tabControl1.SelectedIndex = 0;
            }
            if (InvokeRequired)
            {
                BeginInvoke(update);
                return;
            }
            update();
        }

        private void client_RoomChanged(object? sender, RoomChangedEventArgs e)
        {
            if (_client == null)
                return;

            void update()
            {
                if (_client.Room != null && _lobbyPage.Parent == null)
                {
                    showLobbyTab();
                }
                if (_client.Room == null && _lobbyPage.Parent != null)
                {
                    hideLobbyTab();
                }
                _clientStatusTextBox.Text = _client.GetConnectionStatusString();
            }
            if (InvokeRequired)
            {
                BeginInvoke(update);
                return;
            }
            update();
        }

        private void client_OnStatus(object? sender, StringEventArgs e)
        {
            BeginInvoke(new Action(() =>
            {
                _consoleControl.PrintToConsole(e.Message, Color.LightBlue);
                _clientStatusTextBox.Text = _client.GetConnectionStatusString();
            }));
        }

        private void client_OnError(object? sender, StringEventArgs e)
        {
            BeginInvoke(new Action(() =>
            {
                _consoleControl.PrintToConsole(e.Message, Color.Red);
                _clientStatusTextBox.Text = _client.GetConnectionStatusString();
            }));
        }

        private void default_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Properties.Settings.Default.ControlBackColor))
            {
                BackColor = Properties.Settings.Default.ControlBackColor;
                _consoleControl.BackColor = Properties.Settings.Default.ControlBackColor;
                _lobbyControl.BackColor = Properties.Settings.Default.ControlBackColor;
            }
            if (e.PropertyName == nameof(Properties.Settings.Default.HostServerOnLaunch))
            {
                if (_server == null && Properties.Settings.Default.HostServerOnLaunch)
                    hostServer();
            }
        }

        private void hostServer()
        {
            if (_server == null)
            {
                _server = new Server(Properties.Settings.Default.Port);
                _server.OnStatus += server_OnStatus;
                _server.OnError += server_OnError;
                _server.Host();
            }
        }

        private async Task<ConnectionResult> initClientAsync(string address, int port)
        {
            if (_client.IsConnected == true)
                await _client.Disconnect();
            updateButtonAvailability();

            var ipendpoint = Neto.Client.NetoClient.EndPointFromAddress(address, port, out string error);
            if (ipendpoint == null)
            {
                MessageBox.Show(this, error, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return ConnectionResult.Denied;
            }
            else
            {
                var connectResult = await _client.Connect(address, port);
                updateButtonAvailability();
                return connectResult;
            }
        }

        private void listenToSettingsChanged()
        {
            Properties.Settings.Default.PropertyChanged += default_PropertyChanged;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Properties.Settings.Default.Save();
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            if (Properties.Settings.Default.HostServerOnLaunch)
            {
                hostServer();
            }

            var c = Properties.Settings.Default.ControlBackColor;

            _consoleControl.BackColor = c;
            _lobbyControl.BackColor = c;
            _lobbyControl.HandleCreated += _lobbyControl_HandleCreated;
            //Select (and initialize) the lobby control
            tabControl1.SelectedIndex = 1;

            updateButtonAvailability();

            //Set the initial status of the status text box
            _clientStatusTextBox.Text = _client.GetConnectionStatusString();

            //Start looking for Elden Ring process
            _processHandler.StartScan();

            if (Properties.Settings.Default.AutoConnect && !string.IsNullOrWhiteSpace(Properties.Settings.Default.ServerAddress))
            {
                await connect(Properties.Settings.Default.ServerAddress, Properties.Settings.Default.Port);
            }
        }

        private void _lobbyControl_HandleCreated(object? sender, EventArgs e)
        {
            //Make sure the lobby control has been visible once, so it's controls are initialized
            tabControl1.TabPages.Remove(_lobbyPage);
            tabControl1.SelectedIndex = 0;
            _lobbyControl.HandleCreated -= _lobbyControl_HandleCreated;
            _lobbyControl.Client = _client;
        }

        private void mainForm_SizeChanged(object? sender, EventArgs e)
        {
            var scale = this.DefaultScaleFactors();
            //We store the Width and Height in 96 DPI scale, so need to convert the window width and height
            Properties.Settings.Default.MainWindowSizeX = Convert.ToInt32(Width / scale.Width);
            Properties.Settings.Default.MainWindowSizeY = Convert.ToInt32(Height / scale.Height);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY_MSG_ID)
            {
                HotkeyPressed?.Invoke(this, new HotkeyEventArgs(m.WParam.ToInt32()));
            }
            base.WndProc(ref m);
        }

        private void openMapWindow()
        {
            if (_mapWindowThread?.ThreadState == ThreadState.Running)
                return;

            if (_mapCoordinateProviderHandler != null)
            {
                _mapCoordinateProviderHandler.Dispose();
                _mapCoordinateProviderHandler = null;
            }

            _mapWindowThread = new Thread(() =>
            {
                Vector2u windowSize;
                if (Properties.Settings.Default.MapWindowCustomSize && Properties.Settings.Default.MapWindowWidth >= 0 && Properties.Settings.Default.MapWindowHeight >= 0)
                {
                    windowSize = new Vector2u((uint)Properties.Settings.Default.MapWindowWidth, (uint)Properties.Settings.Default.MapWindowHeight);
                }
                else if (!Properties.Settings.Default.MapWindowCustomSize && Properties.Settings.Default.MapWindowLastWidth >= 0 && Properties.Settings.Default.MapWindowLastHeight >= 0)
                {
                    windowSize = new Vector2u((uint)Properties.Settings.Default.MapWindowLastWidth, (uint)Properties.Settings.Default.MapWindowLastHeight);
                }
                else
                {
                    windowSize = new Vector2u(500, 500);
                }
                _mapWindow = new MapWindow(windowSize.X, windowSize.Y);
                if (Properties.Settings.Default.MapWindowCustomPosition && Properties.Settings.Default.MapWindowX >= 0 && Properties.Settings.Default.MapWindowY >= 0)
                {
                    _mapWindow.Position = new Vector2i(Properties.Settings.Default.MapWindowX, Properties.Settings.Default.MapWindowY);
                }
                else
                {
                    _mapWindow.Position = new Vector2i(Left + Width, Top);
                }
                _mapCoordinateProviderHandler = new MapCoordinateProviderHandler(_mapWindow, _processHandler, _client);
                _mapWindow.Start();
            });
            _mapWindowThread.Start();
        }

        private void removeClientListeners(Client? client)
        {
            if (client == null)
                return;

            client.Connected -= client_Connected;
            client.Disconnected -= client_Disconnected;
            client.OnRoomChanged -= client_RoomChanged;
            client.RemoveListener<ServerJoinRoomAccepted>(joinRoomAccepted);
            client.RemoveListener<ServerJoinRoomDenied>(joinRoomDenied);
            client.RemoveListener<ServerEntireBingoBoardUpdate>(gotBingoBoard);
        }

        private void server_OnStatus(object? sender, StringEventArgs e)
        {
            _consoleControl.PrintToConsole(e.Message, Color.Orange);
        }

        private void server_OnError(object? sender, StringEventArgs e)
        {
            _consoleControl.PrintToConsole(e.Message, Color.Red);
        }

        private async Task tryStartingGameWithoutEAC()
        {
            if (_processHandler == null)
                return;

            try
            {
                GameRunningStatus res = await _processHandler.GetGameRunningStatus();
                if (res == GameRunningStatus.NotRunning)
                {
                    await _processHandler.SafeStartGame();
                }
                else if (res == GameRunningStatus.RunningWithEAC)
                {
                    if (IsAdministrator())
                    {
                        DialogResult result = MessageBox.Show("Game is already running with EAC active!\n\n" +
                            "Do you want to close and restart it in offline mode without EAC?\n\n", Application.ProductName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                        if (result == DialogResult.Yes)
                        {
                            _processHandler.KillGameAndEAC();
                            await Task.Delay(2000);
                            await _processHandler.SafeStartGame();
                        }
                    }
                    else
                    {
                        MessageBox.Show("Game is already running with EAC active!\n\n" +
                            "Restart this application as administrator if you want it to be able to restart Elden Ring without EAC", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            } 
            catch(Exception e)
            {
                MessageBox.Show($"Error starting the game: {e.Message}", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            //Do nothing if game is already running without EAC
        }

        private void updateButtonAvailability()
        {
            BeginInvoke(new Action(() =>
            {
                bool connected = _client?.IsConnected == true;
                var connectingOrConnected = _connecting || connected;
                _connectButton.Visible = !connectingOrConnected;
                _disconnectButton.Visible = connectingOrConnected;
                
                toolStripSeparator1.Visible = !connected;

                bool inRoom = _client?.Room != null;
                _createLobbyButton.Visible = !inRoom;
                _joinLobbyButton.Visible = !inRoom;
                _leaveRoomButton.Visible = inRoom;

                _createLobbyButton.Enabled = connected;
                _joinLobbyButton.Enabled = connected;
                _leaveRoomButton.Enabled = connected;
            }));
        }
    }
}