﻿/*
rF2SMMonitor is visual debugger for rF2 Shared Memory Plugin.

MainForm implementation, contains main loop and render calls.

Author: The Iron Wolf (vleonavicius@hotmail.com)
*/
using rF2SMMonitor.rFactor2Data;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using static rF2SMMonitor.rFactor2Constants;

namespace rF2SMMonitor
{
  public partial class MainForm : Form
  {
    // Connection fields
    private const int CONNECTION_RETRY_INTERVAL_MS = 1000;
    private const int DISCONNECTED_CHECK_INTERVAL_MS = 15000;
    private const float DEGREES_IN_RADIAN = 57.2957795f;
    private const int LIGHT_MODE_REFRESH_MS = 500;
    System.Windows.Forms.Timer connectTimer = new System.Windows.Forms.Timer();
    System.Windows.Forms.Timer disconnectTimer = new System.Windows.Forms.Timer();
    bool connected = false;

    // Shared memory buffer fields
    readonly int SHARED_MEMORY_SIZE_BYTES = Marshal.SizeOf(typeof(rF2State));
    readonly int SHARED_MEMORY_HEADER_SIZE_BYTES = Marshal.SizeOf(typeof(rF2StateHeader));

    readonly int RF2_TELEMETRY_BUFFER_SIZE_BYTES = Marshal.SizeOf(typeof(rF2Telemetry));
    readonly int RF2_SCORING_BUFFER_SIZE_BYTES = Marshal.SizeOf(typeof(rF2Scoring));
    readonly int RF2_EXTENDED_BUFFER_SIZE_BYTES = Marshal.SizeOf(typeof(rF2Extended));
    readonly int RF2_BUFFER_HEADER_SIZE_BYTES = Marshal.SizeOf(typeof(rF2BufferHeader));

    byte[] sharedMemoryReadBufferTelemetry = null;
    byte[] sharedMemoryReadBufferScoring = null;
    byte[] sharedMemoryReadBufferExtended = null;

    // Plugin access fields
    // Telemetry:
    Mutex mutexTelemetry = null;
    MemoryMappedFile memoryMappedTelemetry1 = null;
    MemoryMappedFile memoryMappedTelemetry2 = null;

    // Scoring:
    Mutex mutexScoring = null;
    MemoryMappedFile memoryMappedScoring1 = null;
    MemoryMappedFile memoryMappedScoring2 = null;

    // Extended:
    Mutex mutexExtended = null;
    MemoryMappedFile memoryMappedExtended1 = null;
    MemoryMappedFile memoryMappedExtended2 = null;

    // Marshalled view
    rF2State currrF2State;

    // Interpolation debugging
    InterpolationStats interpolationStats = new InterpolationStats();

    // Track rF2 transitions.
    TransitionTracker tracker = new TransitionTracker();

    // Config
    IniFile config = new IniFile();
    float scale = 2.0f;
    float xOffset = 0.0f;
    float yOffset = 0.0f;
    int focusVehicle = 0;
    bool centerOnVehicle = true;
    bool rotateAroundVehicle = true;
    bool logPhaseAndState = true;
    bool logDamage = true;
    bool logTiming = true;
    bool logLightMode = false;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMessage
    {
      public IntPtr Handle;
      public uint Message;
      public IntPtr WParameter;
      public IntPtr LParameter;
      public uint Time;
      public Point Location;
    }

    [DllImport("user32.dll")]
    public static extern int PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    public MainForm()
    {
      this.InitializeComponent();

      this.DoubleBuffered = true;
      this.StartPosition = FormStartPosition.Manual;
      this.Location = new Point(0, 0);

      this.EnableControls(false);
      this.scaleTextBox.KeyDown += TextBox_KeyDown;
      this.scaleTextBox.LostFocus += ScaleTextBox_LostFocus;
      this.xOffsetTextBox.KeyDown += TextBox_KeyDown;
      this.xOffsetTextBox.LostFocus += XOffsetTextBox_LostFocus;
      this.yOffsetTextBox.KeyDown += TextBox_KeyDown;
      this.yOffsetTextBox.LostFocus += YOffsetTextBox_LostFocus;
      this.focusVehTextBox.KeyDown += TextBox_KeyDown;
      this.focusVehTextBox.LostFocus += FocusVehTextBox_LostFocus;
      this.setAsOriginCheckBox.CheckedChanged += SetAsOriginCheckBox_CheckedChanged;
      this.rotateAroundCheckBox.CheckedChanged += RotateAroundCheckBox_CheckedChanged;
      this.checkBoxLogPhaseAndState.CheckedChanged += CheckBoxLogPhaseAndState_CheckedChanged;
      this.checkBoxLogDamage.CheckedChanged += CheckBoxLogDamage_CheckedChanged;
      this.checkBoxLogTiming.CheckedChanged += CheckBoxLogTiming_CheckedChanged;
      this.checkBoxLightMode.CheckedChanged += CheckBoxLightMode_CheckedChanged;
      this.MouseWheel += MainForm_MouseWheel;

      this.LoadConfig();
      this.connectTimer.Interval = CONNECTION_RETRY_INTERVAL_MS;
      this.connectTimer.Tick += ConnectTimer_Tick;
      this.disconnectTimer.Interval = DISCONNECTED_CHECK_INTERVAL_MS;
      this.disconnectTimer.Tick += DisconnectTimer_Tick;
      this.connectTimer.Start();
      this.disconnectTimer.Start();

      this.view.BorderStyle = BorderStyle.Fixed3D;
      this.view.Paint += View_Paint;
      this.MouseClick += MainForm_MouseClick;
      this.view.MouseClick += MainForm_MouseClick;

      Application.Idle += HandleApplicationIdle;
    }

    private void CheckBoxLightMode_CheckedChanged(object sender, EventArgs e)
    {
      this.logLightMode = this.checkBoxLightMode.Checked;
      
      // Disable/enable rendering options
      this.globalGroupBox.Enabled = !this.logLightMode;
      this.groupBoxFocus.Enabled = !this.logLightMode;

      this.config.Write("logLightMode", this.logLightMode ? "1" : "0");
    }

    private void CheckBoxLogDamage_CheckedChanged(object sender, EventArgs e)
    {
      this.logDamage = this.checkBoxLogDamage.Checked;
      this.config.Write("logDamage", this.logDamage ? "1" : "0");
    }

    private void CheckBoxLogTiming_CheckedChanged(object sender, EventArgs e)
    {
      this.logTiming = this.checkBoxLogTiming.Checked;
      this.config.Write("logTiming", this.logTiming ? "1" : "0");
    }

    private void CheckBoxLogPhaseAndState_CheckedChanged(object sender, EventArgs e)
    {
      this.logPhaseAndState = this.checkBoxLogPhaseAndState.Checked;
      this.config.Write("logPhaseAndState", this.logPhaseAndState ? "1" : "0");
    }

    private void MainForm_MouseClick(object sender, MouseEventArgs e)
    {
      if (e.Button == MouseButtons.Right)
        this.interpolationStats.Reset();
    }

    private void YOffsetTextBox_LostFocus(object sender, EventArgs e)
    {
      float result = 0.0f;
      if (float.TryParse(this.yOffsetTextBox.Text, out result))
      {
        this.yOffset = result;
        this.config.Write("yOffset", this.yOffset.ToString());
      }
      else
        this.yOffsetTextBox.Text = this.yOffset.ToString();

    }

    private void XOffsetTextBox_LostFocus(object sender, EventArgs e)
    {
      float result = 0.0f;
      if (float.TryParse(this.xOffsetTextBox.Text, out result))
      {
        this.xOffset = result;
        this.config.Write("xOffset", this.xOffset.ToString());
      }
      else
        this.xOffsetTextBox.Text = this.xOffset.ToString();

    }

    private void MainForm_MouseWheel(object sender, MouseEventArgs e)
    {
      float step = 0.5f;
      if (this.scale < 5.0f)
        step = 0.25f;
      else if (this.scale < 2.0f)
        step = 0.1f;
      else if (this.scale < 1.0f)
        step = 0.05f;

      if (e.Delta > 0)
        this.scale += step;
      else if (e.Delta < 0)
        this.scale -= step;

      if (this.scale <= 0.0f)
        this.scale = 0.05f;

      this.config.Write("scale", this.scale.ToString());
      this.scaleTextBox.Text = this.scale.ToString();
    }

    private void RotateAroundCheckBox_CheckedChanged(object sender, EventArgs e)
    {
      this.rotateAroundVehicle = this.rotateAroundCheckBox.Checked;
      this.config.Write("rotateAroundVehicle", this.rotateAroundVehicle ? "1" : "0");
    }

    private void SetAsOriginCheckBox_CheckedChanged(object sender, EventArgs e)
    {
      this.centerOnVehicle = this.setAsOriginCheckBox.Checked;
      this.rotateAroundCheckBox.Enabled = this.setAsOriginCheckBox.Checked;
      this.config.Write("centerOnVehicle", this.centerOnVehicle ? "1" : "0");
    }

    private void FocusVehTextBox_LostFocus(object sender, EventArgs e)
    {
      int result = 0;
      if (int.TryParse(this.focusVehTextBox.Text, out result) && result >= 0)
      {
        this.focusVehicle = result;
        this.config.Write("focusVehicle", this.focusVehicle.ToString());
      }
      else
        this.focusVehTextBox.Text = this.focusVehTextBox.ToString();
    }

    private void ScaleTextBox_LostFocus(object sender, EventArgs e)
    {
      float result = 0.0f;
      if (float.TryParse(this.scaleTextBox.Text, out result))
      {
        this.scale = result;
        this.config.Write("scale", this.scale.ToString());
      }
      else
        this.scaleTextBox.Text = this.scale.ToString();
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.KeyCode == Keys.Enter)
        this.view.Focus();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing && (components != null))
        components.Dispose();
      
      if (disposing)
        Disconnect();

      base.Dispose(disposing);
    }

    // Amazing loop implementation by Josh Petrie from:
    // http://gamedev.stackexchange.com/questions/67651/what-is-the-standard-c-windows-forms-game-loop
    bool IsApplicationIdle()
    {
      NativeMessage result;
      return PeekMessage(out result, IntPtr.Zero, (uint)0, (uint)0, (uint)0) == 0;
    }

    void HandleApplicationIdle(object sender, EventArgs e)
    {
      while (this.IsApplicationIdle())
      {
        this.MainUpdate();

        if (base.WindowState == FormWindowState.Minimized)
        {
          // being lazy lazy lazy.
          this.tracker.TrackPhase(ref this.currrF2State, null, this.logPhaseAndState);
          this.tracker.TrackDamage(ref this.currrF2State, null, this.logDamage);
          this.tracker.TrackTimings(ref this.currrF2State, null, this.logTiming);
        }
        else
        {
          this.MainRender();
        }

        if (this.logLightMode)
          Thread.Sleep(LIGHT_MODE_REFRESH_MS);
      }
    }

    void MainUpdate()
    {
      if (!this.connected)
        return;

      try
      {
        // Clients that do not need consistency accross the whole buffer, like dashboards, do not need to use mutexes.
        // Note: if it is critical for client minimize wait time, same strategy as plugin uses can be employed.
        // Pass 0 timeout and skip update if someone holds the lock.
        if (this.mutexTelemetry.WaitOne(5000))
        {
          try
          {
            bool buf1Current = false;
            // Try buffer 1:
            using (var sharedMemoryStreamView = this.memoryMappedTelemetry1.CreateViewStream())
            {
              var sharedMemoryStream = new BinaryReader(sharedMemoryStreamView);
              this.sharedMemoryReadBufferTelemetry = sharedMemoryStream.ReadBytes(this.SHARED_MEMORY_HEADER_SIZE_BYTES);

              // Marhsal header
              var headerHandle = GCHandle.Alloc(this.sharedMemoryReadBufferTelemetry, GCHandleType.Pinned);
              var header = (rF2StateHeader)Marshal.PtrToStructure(headerHandle.AddrOfPinnedObject(), typeof(rF2StateHeader));
              headerHandle.Free();

              if (header.mCurrentRead == 1)
              {
                sharedMemoryStream.BaseStream.Position = 0;
                this.sharedMemoryReadBufferTelemetry = sharedMemoryStream.ReadBytes(this.SHARED_MEMORY_SIZE_BYTES);
                buf1Current = true;
                this.currBuff = 1;
              }
            }

            // Read buffer 2
            if (!buf1Current)
            {
              using (var sharedMemoryStreamView = this.memoryMappedTelemetry2.CreateViewStream())
              {
                var sharedMemoryStream = new BinaryReader(sharedMemoryStreamView);
                this.sharedMemoryReadBufferTelemetry = sharedMemoryStream.ReadBytes(this.SHARED_MEMORY_SIZE_BYTES);
                this.currBuff = 2;
              }
            }
          }
          finally
          {
            this.mutexTelemetry.ReleaseMutex();
          }

          // Marshal rF2State
          var handle = GCHandle.Alloc(this.sharedMemoryReadBufferTelemetry, GCHandleType.Pinned);
          this.currrF2State = (rF2State)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(rF2State));
          handle.Free();
        }
      }
      catch (Exception)
      {
        this.Disconnect();
      }
    }

    void MainRender()
    {
      this.view.Refresh();
    }

    int framesAvg = 20;
    int frame = 0;
    int fps = 0;
    Stopwatch fpsStopWatch = new Stopwatch();
    private void UpdateFPS()
    {
      if (this.frame > this.framesAvg)
      {
        this.fpsStopWatch.Stop();
        var tsSinceLastRender = this.fpsStopWatch.Elapsed;
        this.fps = tsSinceLastRender.Milliseconds > 0 ? (1000 * this.framesAvg) / tsSinceLastRender.Milliseconds : 0;
        this.fpsStopWatch.Restart();
        this.frame = 0;
      }
      else
        ++this.frame;
    }

    private static string getStringFromBytes(byte[] bytes)
    {
      return bytes == null ? "" : Encoding.Default.GetString(bytes).TrimEnd('\0').Trim();
    }

    // Corrdinate conversion:
    // rF2 +x = screen +x
    // rF2 +z = screen -z
    // rF2 +yaw = screen -yaw
    // If I don't flip z, the projection will look from below.
    void View_Paint(object sender, PaintEventArgs e)
    {
      var g = e.Graphics;

      this.tracker.TrackPhase(ref this.currrF2State, g, this.logPhaseAndState);
      this.tracker.TrackDamage(ref this.currrF2State, g, this.logDamage);
      this.tracker.TrackTimings(ref this.currrF2State, g, this.logTiming);

      this.UpdateFPS();

      if (!this.connected)
      {
        var brush = new SolidBrush(System.Drawing.Color.Black);
        g.DrawString("Not connected", SystemFonts.DefaultFont, brush, 3.0f, 3.0f);

        if (this.logLightMode)
          return;
      }
      else
      {
        var brush = new SolidBrush(System.Drawing.Color.Green);

        var currX = 3.0f;
        var currY = 3.0f;
        float yStep = SystemFonts.DefaultFont.Height;
        var gameStateText = new StringBuilder();
        gameStateText.Append(
          $"Plugin Version:    Expected: 1.1.0.1    Actual: {MainForm.getStringFromBytes(this.currrF2State.mVersion)}    FPS: {this.fps}");

        // Draw header
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX, currY);

        gameStateText.Clear();

        gameStateText.Append(
          "mElapsedTime:\n"
          + "mCurrentET:\n"
          + "mElapsedTime-mCurrentET:\n"
          + "mDetlaTime:\n"
          + "mInvulnerable:\n"
          + "mVehicleName:\n"
          + "mTrackName:\n"
          + "mLapStartET:\n"
          + "mLapDist:\n"
          + "mEndET:\n");

        // Col 1 labels
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX, currY += yStep);

        gameStateText.Clear();

        gameStateText.Append(
                $"{this.currrF2State.mElapsedTime:N3}\n"
                + $"{this.currrF2State.mCurrentET:N3}\n"
                + $"{(this.currrF2State.mElapsedTime - this.currrF2State.mCurrentET):N3}\n"
                + $"{this.currrF2State.mDeltaTime:N3}\n"
                + (this.currrF2State.mInvulnerable == 0 ? "off" : "on") + "\n"
                + $"{MainForm.getStringFromBytes(this.currrF2State.mVehicleName)}\n"
                + $"{MainForm.getStringFromBytes(this.currrF2State.mTrackName)}\n"
                + $"{this.currrF2State.mLapStartET:N3}\n"
                + $"{this.currrF2State.mLapDist:N3}\n"
                + (this.currrF2State.mEndET < 0.0 ? "Unknown" : this.currrF2State.mEndET.ToString("N3")) + "\n");

        // Col1 values
        g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, currX + 145, currY);

        if (this.currrF2State.mNumVehicles > 0)
        {
          gameStateText.Clear();

          gameStateText.Append(
            "mLapDist(Plr):\n"
            + "mTimeIntoLap:\n"
            + "mEstimatedLapTime:\n"
            + "mTimeBehindNext:\n"
            + "mTimeBehindLeader:\n"
            + "mPlayerName:\n"
            + "mPlrFileName:\n"
            + "mPitGroup\n");

          // Col 2 labels
          g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, brush, currX += 275, currY);
          gameStateText.Clear();

          var plrVeh = this.currrF2State.mVehicles[0];
          gameStateText.Append(
            $"{plrVeh.mLapDist:N3}\n"
            + $"{plrVeh.mTimeIntoLap:N3}\n"
            + $"{plrVeh.mEstimatedLapTime:N3}\n"
            + $"{plrVeh.mTimeBehindNext:N3}\n"
            + $"{plrVeh.mTimeBehindLeader:N3}\n"
            + $"{MainForm.getStringFromBytes(this.currrF2State.mPlayerName)}\n"
            + $"{MainForm.getStringFromBytes(this.currrF2State.mPlrFileName)}\n"
            + $"{MainForm.getStringFromBytes(plrVeh.mPitGroup)}\n");

          // Col2 values
          g.DrawString(gameStateText.ToString(), SystemFonts.DefaultFont, Brushes.Purple, currX + 120, currY);
        }



        if (this.logLightMode)
          return;

        Interpolator.RenderDebugInfo(ref this.currrF2State, g);

        this.interpolationStats.RenderInterpolationInfo(ref this.currrF2State, g, this.currBuff);

        // Branch of UI choice: origin center or car# center
        // Fix rotation on car of choice or no.
        // Draw axes
        // Scale will be parameter, scale applied last on render to zoom.
        float scale = this.scale;

        var xVeh = (float)this.currrF2State.mPos.x;
        var zVeh = (float)this.currrF2State.mPos.z;
        var yawVeh = Math.Atan2(this.currrF2State.mOri[2].x, this.currrF2State.mOri[2].z);

         // View center
        var xScrOrigin = this.view.Width / 2.0f;
        var yScrOrigin = this.view.Height / 2.0f;
        if (!this.centerOnVehicle)
        {
          // Set world origin.
          g.TranslateTransform(xScrOrigin, yScrOrigin);
          this.RenderOrientationAxis(g);
          g.ScaleTransform(scale, scale);

          RenderCar(g, xVeh, -zVeh, -(float)yawVeh, Brushes.Green);

          for (int i = 0; i < this.currrF2State.mNumVehicles; ++i)
            this.RenderCar(g,
              (float)this.currrF2State.mVehicles[i].mPos.x,
              -(float)this.currrF2State.mVehicles[i].mPos.z,
              -(float)this.currrF2State.mVehicles[i].mYaw, Brushes.Red);
        }
        else
        {
          g.TranslateTransform(xScrOrigin, yScrOrigin);

          if (this.rotateAroundVehicle)
            g.RotateTransform(180.0f + (float)yawVeh * DEGREES_IN_RADIAN);

          this.RenderOrientationAxis(g);
          g.ScaleTransform(scale, scale);
          g.TranslateTransform(-xVeh, zVeh);

          RenderCar(g, xVeh, -zVeh, -(float)yawVeh, Brushes.Green);

          for (int i = 0; i < this.currrF2State.mNumVehicles; ++i)
            this.RenderCar(g,
              (float)this.currrF2State.mVehicles[i].mPos.x,
              -(float)this.currrF2State.mVehicles[i].mPos.z,
             -(float)this.currrF2State.mVehicles[i].mYaw, Brushes.Red);
        }
      }
    }


    // Length
    // 174.6in (4,435mm)
    // 175.6in (4,460mm) (Z06, ZR1)
    // Width
    // 72.6in (1,844mm)
    // 75.9in (1,928mm) (Z06, ZR1)
    /*PointF[] carPoly =
    {
        new PointF(0.922f, 2.217f),
        new PointF(0.922f, -1.4f),
        new PointF(1.3f, -1.4f),
        new PointF(0.0f, -2.217f),
        new PointF(-1.3f, -1.4f),
        new PointF(-0.922f, -1.4f),
        new PointF(-0.922f, 2.217f),
      };*/

    PointF[] carPoly =
    {
      new PointF(-0.922f, -2.217f),
      new PointF(-0.922f, 1.4f),
      new PointF(-1.3f, 1.4f),
      new PointF(0.0f, 2.217f),
      new PointF(1.3f, 1.4f),
      new PointF(0.922f, 1.4f),
      new PointF(0.922f, -2.217f),
    };

    private void RenderCar(Graphics g, float x, float y, float yaw, Brush brush)
    {
      var state = g.Save();

      g.TranslateTransform(x, y);

      g.RotateTransform(yaw * DEGREES_IN_RADIAN);

      g.FillPolygon(brush, this.carPoly);

      g.Restore(state);
    }

    static float arrowSide = 10.0f;
    PointF[] arrowHead =
    {
      new PointF(-arrowSide / 2.0f, -arrowSide / 2.0f),
      new PointF(0.0f, arrowSide / 2.0f),
      new PointF(arrowSide / 2.0f, -arrowSide / 2.0f)
    };

    private void RenderOrientationAxis(Graphics g)
    {

      float length = 1000.0f;
      float arrowDistX = this.view.Width / 2.0f - 10.0f;
      float arrowDistY = this.view.Height / 2.0f - 10.0f;

      // X (x screen) axis
      g.DrawLine(Pens.Red, -length, 0.0f, length, 0.0f);
      var state = g.Save();
      g.TranslateTransform(this.rotateAroundVehicle ? arrowDistY : arrowDistX, 0.0f);
      g.RotateTransform(-90.0f);
      g.FillPolygon(Brushes.Red, this.arrowHead);
      g.RotateTransform(90.0f);
      g.DrawString("x+", SystemFonts.DefaultFont, Brushes.Red, -10.0f, 10.0f);
      g.Restore(state);

      state = g.Save();
      // Z (y screen) axis
      g.DrawLine(Pens.Blue, 0.0f, -length, 0.0f, length);
      g.TranslateTransform(0.0f, -arrowDistY);
      g.RotateTransform(180.0f);
      g.FillPolygon(Brushes.Blue, this.arrowHead);
      g.DrawString("z+", SystemFonts.DefaultFont, Brushes.Blue, 10.0f, -10.0f);

      g.Restore(state);
    }

    private void ConnectTimer_Tick(object sender, EventArgs e)
    {
      if (!this.connected)
      {
        try
        {
          this.mutexTelemetry = Mutex.OpenExisting(rF2SMMonitor.rFactor2Constants.MM_TELEMETRY_FILE_ACCESS_MUTEX);
          this.memoryMappedTelemetry1 = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_TELEMETRY_FILE_NAME1);
          this.memoryMappedTelemetry2 = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_TELEMETRY_FILE_NAME2);

          // NOTE: Make sure that RF2_TELEMETRY_BUFFER_SIZE_BYTES matches the structure size in the plugin (debug mode prints that).
          this.sharedMemoryReadBufferTelemetry = new byte[this.RF2_TELEMETRY_BUFFER_SIZE_BYTES];


          this.mutexScoring = Mutex.OpenExisting(rF2SMMonitor.rFactor2Constants.MM_SCORING_FILE_ACCESS_MUTEX);
          this.memoryMappedScoring1 = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_SCORING_FILE_NAME1);
          this.memoryMappedScoring2 = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_SCORING_FILE_NAME2);

          // NOTE: Make sure that RF2_SCORING_BUFFER_SIZE_BYTES matches the structure size in the plugin (debug mode prints that).
          this.sharedMemoryReadBufferScoring = new byte[this.RF2_SCORING_BUFFER_SIZE_BYTES];


          this.mutexExtended = Mutex.OpenExisting(rF2SMMonitor.rFactor2Constants.MM_EXTENDED_FILE_ACCESS_MUTEX);
          this.memoryMappedExtended1 = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_EXTENDED_FILE_NAME1);
          this.memoryMappedExtended2 = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_EXTENDED_FILE_NAME2);

          // NOTE: Make sure that RF2_EXTENDED_BUFFER_SIZE_BYTES matches the structure size in the plugin (debug mode prints that).
          this.sharedMemoryReadBufferExtended = new byte[this.RF2_EXTENDED_BUFFER_SIZE_BYTES];

          this.connected = true;

          this.EnableControls(true);
        }
        catch (Exception)
        {
          Disconnect();
        }
      }
    }

    private void DisconnectTimer_Tick(object sender, EventArgs e)
    {
      if (!this.connected)
        return;

      try
      {
        // Alternatively, I could release resources and try re-acquiring them immidiately.
        var processes = Process.GetProcessesByName(rF2SMMonitor.rFactor2Constants.RFACTOR2_PROCESS_NAME);
        if (processes.Length == 0)
          Disconnect();
      }
      catch (Exception)
      {
        Disconnect();
      }
    }

    private void DisposeAndClear(ref MemoryMappedFile file1, ref MemoryMappedFile file2, ref Mutex mutex, ref byte[] buffer)
    {
      if (file1 != null)
        file1.Dispose();

      if (file2 != null)
        file2.Dispose();

      if (mutex != null)
        mutex.Dispose();

      file1 = null;
      file2 = null;
      buffer = null;
      mutex = null;
    }

    private void Disconnect()
    {
      this.DisposeAndClear(ref this.memoryMappedTelemetry1, ref this.memoryMappedTelemetry2, ref this.mutexTelemetry, ref this.sharedMemoryReadBufferTelemetry);
      this.DisposeAndClear(ref this.memoryMappedScoring1, ref this.memoryMappedScoring2, ref this.mutexScoring, ref this.sharedMemoryReadBufferScoring);
      this.DisposeAndClear(ref this.memoryMappedExtended1, ref this.memoryMappedExtended2, ref this.mutexExtended, ref this.sharedMemoryReadBufferExtended);

      this.connected = false;

      this.EnableControls(false);
    }

    void EnableControls(bool enable)
    {
      this.globalGroupBox.Enabled = enable;
      this.groupBoxFocus.Enabled = enable;
      this.groupBoxLogging.Enabled = enable;

      this.focusVehLabel.Enabled = false;
      this.focusVehTextBox.Enabled = false;
      this.xOffsetLabel.Enabled = false;
      this.xOffsetTextBox.Enabled = false;
      this.yOffsetLabel.Enabled = false;
      this.yOffsetTextBox.Enabled = false;


      if (enable)
      {
        this.rotateAroundCheckBox.Enabled = this.setAsOriginCheckBox.Checked;
        this.globalGroupBox.Enabled = !this.logLightMode;
        this.groupBoxFocus.Enabled = !this.logLightMode;
      }
    }

    void LoadConfig()
    {
      float result = 0.0f;
      this.scale = 2.0f;
      if (float.TryParse(this.config.Read("scale"), out result))
        this.scale = result;

      if (this.scale <= 0.0f)
        this.scale = 0.1f;

      this.scaleTextBox.Text = this.scale.ToString();

      result = 0.0f;
      this.xOffset = 0.0f;
      if (float.TryParse(this.config.Read("xOffset"), out result))
        this.xOffset = result;

      this.xOffsetTextBox.Text = this.xOffset.ToString();

      result = 0.0f;
      this.yOffset = 0.0f;
      if (float.TryParse(this.config.Read("yOffset"), out result))
        this.yOffset = result;

      this.yOffsetTextBox.Text = this.yOffset.ToString();

      int intResult = 0;
      this.focusVehicle = 0;
      if (int.TryParse(this.config.Read("focusVehicle"), out intResult) && intResult >= 0)
        this.focusVehicle = intResult;

      this.focusVehTextBox.Text = this.focusVehicle.ToString();

      intResult = 0;
      this.centerOnVehicle = true;
      if (int.TryParse(this.config.Read("centerOnVehicle"), out intResult) && intResult == 0)
        this.centerOnVehicle = false;

      this.setAsOriginCheckBox.Checked = this.centerOnVehicle;

      intResult = 0;
      this.rotateAroundVehicle = true;
      if (int.TryParse(this.config.Read("rotateAroundVehicle"), out intResult) && intResult == 0)
        this.rotateAroundVehicle = false;

      this.rotateAroundCheckBox.Checked = this.rotateAroundVehicle;

      intResult = 0;
      this.logLightMode = false;
      if (int.TryParse(this.config.Read("logLightMode"), out intResult) && intResult == 1)
        this.logLightMode = true;

      this.checkBoxLightMode.Checked = this.logLightMode;

      intResult = 0;
      this.logPhaseAndState = true;
      if (int.TryParse(this.config.Read("logPhaseAndState"), out intResult) && intResult == 0)
        this.logPhaseAndState = false;

      this.checkBoxLogPhaseAndState.Checked = this.logPhaseAndState;

      intResult = 0;
      this.logDamage = true;
      if (int.TryParse(this.config.Read("logDamage"), out intResult) && intResult == 0)
        this.logDamage = false;

      this.checkBoxLogDamage.Checked = this.logDamage;

      intResult = 0;
      this.logTiming = true;
      if (int.TryParse(this.config.Read("logTiming"), out intResult) && intResult == 0)
        this.logTiming = false;

      this.checkBoxLogTiming.Checked = this.logTiming;

    }
  }
}