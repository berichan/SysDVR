﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using SysDVR.Client.FileOutput;
using SysDVR.Client.Sources;

namespace SysDVR.Client
{
	class Program
	{
		static string VersionString() 
		{
			var Version = typeof(Program).Assembly.GetName().Version;
			if (Version == null) return "<unknown version>";
			StringBuilder str = new StringBuilder();
			str.Append(Version.Major);
			str.Append(".");
			str.Append(Version.Minor);

			if (Version.Revision != 0)
			{
				str.Append(".");
				str.Append(Version.Revision);
			}

			return str.ToString();
		}

		static void PrintGuide(bool full)
		{
			if (!full) {
				Console.WriteLine("Basic usage:\r\n" +
						"Simply launching this exectuable will show this message and launch the video player via USB.\r\n" +
						"Use 'SysDVR-Client usb' to stream directly, add '--no-audio' or '--no-video' to disable one of the streams\r\n" +
						"To stream in TCP Bridge mode launch 'SysDVR-Client bridge <switch ip address>'\r\n" +
						"There are more advanced options, you can see them with 'SysDVR-Client --help'\r\n" +
						"Press enter to continue.\r\n");
				Console.ReadLine();
				return;
			}

			Console.WriteLine(
@"Usage:
SysDVR-Client.exe <Stream source> [Source options] [Stream options] [Output options]

Stream sources:
	The source mode is how the client connects to SysDVR running on the console. Make sure to set the correct mode with SysDVR-Settings.
	`usb` : Connects to SysDVR via USB, used if no source is specified. Remember to setup the driver as explained on the guide
	`bridge <IP address>` : Connects to SysDVR via network at the specified IP address, requires a strong connection between the PC and switch (LAN or full signal wireless)
	Note that the `Simple network mode` option in SysDVR-Settings does not require the client, you must open it directly in a video player.

Source options:
	`--print-stats` : Logs received data size and errors
	`--no-winusb` : Forces the LibUsb backend on windows, you must use this option in case you installed LibUsb-win32 as the SysDVR driver (it's recommended to use WinUsb)
	`--usb-warn` : Enables printing warnings from the usb stack, use it to debug USB issues
	`--usb-debug` : Same as `--usb-warn` but more detailed
	`--usb-serial NX0000000` : When multiple consoles are plugged in via USB use this option to automatically select one by serial number. 
		This also matches partial serials starting from the end, for example NX012345 will be matched by doing --usb-serial 45

Stream options:
	`--no-video` : Disable video streaming, only streams audio
	`--no-audio` : Disable audio streaming, only streams video

Output options:
	If you don't specify any option the built-in video player will be used.
	Built-in player options:
	`--hw-acc` : Try to use hardware acceleration for decoding, this option uses the first detected decoder, it's recommended to manually specify the decoder name with --decoder
	`--decoder <name>` : Use a specific decoder for ffmpeg decoding, you can see all supported codecs with --show-codecs
	`--scale <quality>` : Use a specific quality for scaling, possible values are `nearest`, `linear` and `best`. `best` may not be available on all PCs, see SDL docs for SDL_HINT_RENDER_SCALE_QUALITY, `linear` is the default mode.
	`--fullscreen` : Start in full screen mode. Press F11 to toggle manually

	RTSP options:
	`--rtsp` : Relay the video feed via RTSP. SysDVR-Client will act as an RTSP server, you can connect to it with RTSP with any compatible video player like mpv or vlc
	`--rtsp-port <port number>` : Port used to stream via RTSP (default is 6666)
	`--rtsp-any-addr` : By default only the pc running SysDVR-Client can connect to the RTSP stream, enable this to allow connections from other devices in your local network

	Low-latency streaming options:
	`--mpv <mpv path>` : Streams the specified channel to mpv via stdin, only works with one channel, if no stream option is specified `--no-audio` will be used.
	`--stdout` : Streams the specified channel to stdout, only works with one channel, if no stream option is specified `--no-audio` will be used.
	
	Storage options
	`--file <output path>` : Saves an mp4 file to the specified folder, existing files will be overwritten.	

Extra options:
	These options will not stream, they just print the output and then quit.
	`--show-decoders` : Prints all video codecs available for the built-in video player
	`--version` : Prints the version

Command examples:
	SysDVR-Client.exe usb
		Connects to switch via USB and streams video and audio in the built-in player

	SysDVR-Client.exe usb --rtsp
		Connects to switch via USB and streams video and audio via rtsp at rtsp://127.0.0.1:6666/
		
	SysDVR-Client.exe bridge 192.168.1.20 --no-video --rtsp-port 9090
		Connects to switch via network at 192.168.1.20 and streams the audio over rtsp at rtsp://127.0.0.1:9090/

	SysDVR-Client.exe usb --mpv `C:\Program Files\mpv\mpv.com`
		Connects to switch via USB and streams the video in low-latency mode via mpv
");
		}

		static void Main(string[] args)
		{
			try 
			{
				// Not pretty but at least won't segfault and generate crash dumps
				InnerMain(args);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex);
			}
		}

		static void InnerMain(string[] args)
		{
			bool HasArg(string arg) => Array.IndexOf(args, arg) != -1;
			bool StreamStdout = HasArg("--stdout");

			if (StreamStdout)
				Console.SetOut(Console.Error);

			Console.WriteLine($"SysDVR-Client - {VersionString()} by exelix");
			Console.WriteLine("https://github.com/exelix11/SysDVR \r\n");
			if (args.Length < 1)
				PrintGuide(false);
			else if (args[0].Contains("help"))
			{
				PrintGuide(true);
				return;
			}

			BaseStreamManager StreamManager;
			bool NoAudio, NoVideo;

			string ArgValue(string arg) 
			{
				int index = Array.IndexOf(args, arg);
				if (index == -1) return null;
				if (args.Length <= index + 1) return null;

				string value = args[index + 1];
				if (!value.Contains(' ') && value.StartsWith('"') && value.EndsWith('"'))
					value = value.Substring(1, value.Length - 2);

				return value;
			}

			int? ArgValueInt(string arg) 
			{
				var a = ArgValue(arg);
				if (int.TryParse(a, out int res))
					return res;
				return null;
			}

			if (HasArg("--version"))
				return;
			else if (HasArg("--show-decoders"))
			{
				Player.LibavUtils.PrintAllCodecs();
				return;
			}

			NoAudio = HasArg("--no-audio");
			NoVideo = HasArg("--no-video");
			StreamThread.Logging = HasArg("--print-stats");

			if (NoVideo && NoAudio)
			{
				Console.WriteLine("Specify at least a video or audio output");
				return;
			}

			if (StreamStdout)
			{
				if (!NoVideo && !NoAudio)
					NoAudio = true;
				StreamManager = new StdOutManager(NoAudio ? StreamKind.Video : StreamKind.Audio);
			}
			else if (HasArg("--mpv"))
			{
				string mpvPath = ArgValue("--mpv");
				if (mpvPath == null || !File.Exists(mpvPath))
				{
					Console.WriteLine("The specified mpv path is not valid");
					return;
				}
				if (!NoVideo && !NoAudio)
					NoAudio = true;
				StreamManager = new MpvStdinManager(NoAudio ? StreamKind.Video : StreamKind.Audio, mpvPath);
			}
			else if (HasArg("--file"))
			{
				string filename = ArgValue("--file");
				if (string.IsNullOrWhiteSpace(filename))
				{
					Console.WriteLine("The specified path is not valid");
					return;
				}
				if (!filename.EndsWith(".mp4", StringComparison.InvariantCultureIgnoreCase))
					Console.WriteLine($"Warning: {filename} doesn't end with .mp4, some programs may not be able to open it if you don't rename it manually.");
				StreamManager = new Mp4OutputManager(filename, !NoVideo, !NoAudio);
			}
#if DEBUG
			else if (HasArg("--debug"))
			{
				string path = ArgValue("--debug");
				StreamManager = new LoggingManager(NoVideo ? null : Path.Combine(path, "video.h264"), NoAudio ? null : Path.Combine(path, "audio.raw"));
			}
#endif
			else if (HasArg("--rtsp"))
			{
				int port = ArgValueInt("--rtsp-port") ?? 6666;
				if (port <= 1024)
					Console.WriteLine("Warning: ports lower than 1024 are usually reserved and may require administrator/root privileges");
				StreamManager = new RTSP.SysDvrRTSPManager(!NoVideo, !NoAudio, !HasArg("--rtsp-any-addr"), port);
			}
			else // Stream to the built-in player by default
			{
				StreamManager = new Player.PlayerManager(!NoVideo, !NoAudio, HasArg("--hw-acc"), ArgValue("--decoder"), ArgValue("--scale"), HasArg("--fullscreen"));
			}

			if (args.Length == 0 || args[0] == "usb")
			{
				var forceLibUsb = HasArg("--no-winusb");
				var warnLevel = UsbContext.LogLevel.Error;

				if (HasArg("--usb-warn")) warnLevel = UsbContext.LogLevel.Warning;
				if (HasArg("--usb-debug")) warnLevel = UsbContext.LogLevel.Debug;

				var ctx = OpenUsbSource(warnLevel, forceLibUsb, ArgValue("--usb-serial"));
				if (ctx == null)
					return;

				if (!NoVideo)
					StreamManager.VideoSource = ctx.MakeStreamingSource(StreamKind.Video);
				if (!NoAudio)
					StreamManager.AudioSource = ctx.MakeStreamingSource(StreamKind.Audio);
			}
			else if (args[0] == "bridge")
			{
				if (args.Length < 2)
				{
					Console.WriteLine("Specify an ip address for bridge mode");
					return;
				}

				string ip = args[1];

				if (!NoVideo)
					StreamManager.VideoSource = new TCPBridgeSource(ip, StreamKind.Video);
				if (!NoAudio)
					StreamManager.AudioSource = new TCPBridgeSource(ip, StreamKind.Audio);
			}
#if DEBUG
			else if (args[0] == "stub")
			{
				StreamManager.VideoSource = new StubSource();
				StreamManager.AudioSource = new StubSource();
			}
			else if (args[0] == "record")
			{
				StreamManager.VideoSource = NoVideo ? null : new RecordedSource(StreamKind.Video);
				StreamManager.AudioSource = NoAudio ? null : new RecordedSource(StreamKind.Audio);
			}
#endif
			else
			{
				Console.WriteLine("Invalid source");
				return;
			}

			new Program().StartStreaming(StreamManager);
		}

		static UsbContext? OpenUsbSource(UsbContext.LogLevel usbLogLeve, bool forceLibUsb, string? preferredSerial)
		{
			var ctx = new UsbContext(usbLogLeve, forceLibUsb);

			var devices = ctx.FindSysdvrDevices();

			if (!string.IsNullOrWhiteSpace(preferredSerial))
				preferredSerial = preferredSerial.ToLower().Trim();
			else 
				preferredSerial = null;

			if (devices.Count == 0)
			{
				Console.WriteLine("ERROR: SysDVR usb device not found.\r\n" +
					"Make sure that SysDVR is running in usb mode on your console and that you installed the correct driver.");
				return null;
			}
			else if (devices.Count == 1)
			{
				if (preferredSerial is not null && devices[0].Item2.EndsWith(preferredSerial))
					Console.WriteLine($"Warning: Connecting to the console with serial {devices[0].Item2} instead of the requested {preferredSerial}");

				Console.WriteLine($"Connecting to the console with serial {devices[0].Item2}...");
				ctx.OpenUsbDevice(devices[0].Item1);
				return ctx;
			}
			else
			{
				var preferred = devices.Where(x => x.Item2.EndsWith(preferredSerial)).ToArray();
				if (preferred.Length == 1)
				{
					ctx.OpenUsbDevice(preferred[0].Item1);
					return ctx;
				}
				// Multiple partial matches ? look for the exact one
				else if (preferred.Length >= 1)
				{
					preferred = devices.Where(x => x.Item2 == preferredSerial).ToArray();
					if (preferred.Length == 1)
					{
						ctx.OpenUsbDevice(preferred[0].Item1);
						return ctx;
					}
					else Console.WriteLine($"Warning: Multiple matches for {preferredSerial}, exact match not found");
				}				
				else Console.WriteLine($"Warning: Requsted serial {preferredSerial} not found");

				Console.WriteLine("Available SysDVR devices:");
				for (int i = 0; i < devices.Count; i++)
					Console.WriteLine($"{i + 1}) {devices[i].Item2}");

				Console.WriteLine("\r\nTIP: You can use the --usb-serial command line option to automatically select one based on the serial number");
			
				select_value:
				Console.Write("Enter the number of the device you want to use: ");
				if (!int.TryParse(Console.ReadLine(), out int selection) || selection < 1 || selection > devices.Count)
				{
					Console.WriteLine($"Error: expected value between 1 and {devices.Count}, try again");
					goto select_value;
				}

				ctx.OpenUsbDevice(devices[selection - 1].Item1);
				return ctx;
			}
		}

		void StartStreaming(BaseStreamManager streams)
		{
			streams.Begin();
			
			void Quit()
			{
				lock (this)
				{
					Console.WriteLine("Terminating threads...");
					streams.Stop();
					if (streams is IDisposable d)
						d.Dispose();
					Environment.Exit(0);
				}
			}

			Console.CancelKeyPress += delegate { Quit(); };

			streams.MainThread();		

			Quit();
		}
	}
}
