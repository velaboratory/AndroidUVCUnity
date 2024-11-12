
using System.Collections.Generic;
using UnityEngine;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine.UI;


public class MjpegServer : MonoBehaviour
{
	List<Thread> threads = new List<Thread>();
	List<AutoResetEvent> events = new List<AutoResetEvent>();
	//UI
	public TMP_Dropdown resolutionDropdown; 
	public TMP_Text cameraText;
	public Slider exposureSlider;
	public Button startButton;
	public Button deleteButton;
	public RawImage rawImage;

	//MJPEG Server
	private HttpListener listener;
	private Thread serverThread;
	AutoResetEvent waitHandle = new AutoResetEvent(false);
	public int port; //set when created

	//Camera access
	public UVCHandler handler; //set after instantiation
	Texture2D cameraTexture; //created when camera starts
	string cameraName; //set upon opening, this is used with handler to call the camera plugin
	byte[] cameraJpeg; //will be jpeg bytes, see next line, don't use until not null
	int cameraJpegActualBytes = 0; //used so we can allocate cameraJpeg only once
	sbyte[] frameData; //will be frame bytes, don't use until not null
	bool started = false; //set when the camera starts streaming frames
	bool opened = false; //set when the camera info is read (resolutions)
	float bandwidth = 1.0f; //1.0 allows for usually just 2 cameras, use .5 for 4, .25 for 8, etc. 

	object jpegLock = new object();
	object frameLock = new object();
	int lastFrameNumber = -1; //used to determine if we have a new frame to read from the plugin

	int width=0;
	int height=0;

	//this function is called by the UVC manager when the user selects a camera
	//it handles permission and filtering of only the mjpeg modes (only mjpeg works atm)
	public void OpenCamera(string cameraName)
	{
		this.cameraName = cameraName;
		if (handler.plugin.Call<bool>("hasPermission", cameraName))
		{
			//we have permission, so we open the camera, which returns all resolutions/fps
			opened = true;
			cameraText.text = cameraName;
			string[] infos = handler.plugin.Call<string[]>("Open", cameraName);
			//we filter out those that begin with 6 (code for mjpeg)
			//and populate the options with width,height,fps
			var options = new List<string>(infos);
			options = options.FindAll((s) => s.StartsWith("6"));
			for (int i = 0; i < options.Count; i++)
			{
				options[i] = options[i].Remove(0, options[i].IndexOf(',') + 1);
			}
			resolutionDropdown.AddOptions(options);
			//and we allow the camera to be started by pressing the button
			if (options.Count > 0)
			{
				startButton.interactable = true;
			}
		}
		else
		{
			//we don't yet have permission.  Calling the obtain permission will pop up a dialog
			//this function will need to be called again, as no callback is used to say when permission is granted
			handler.plugin.Call("ObtainPermission", cameraName);
		}
	}

	public void StartCamera()
	{
		//deal with the cases of the camera not being opened and not be started yet
		if (!opened)
		{
			OpenCamera(cameraName);
			return;
		}
		if (started)
		{
			return;
		}
		if(resolutionDropdown.options.Count == 0)
		{
			return;
		}
		//now parse the resolution
		string info = resolutionDropdown.options[resolutionDropdown.value].text;
		var line = info.Split(","); //width,height,fps
		width = int.Parse(line[0]);
		height = int.Parse(line[1]);
		int fps = int.Parse(line[2]);
		//actually start the camera.  The resolution and fps should work, because the camera publishes them
		int res = handler.plugin.Call<int>("Start", cameraName, width, height, fps, 2, bandwidth);

		if (res == 0) 
		{ 
			started = true;
			cameraTexture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
			rawImage.texture = cameraTexture;
			serverThread = new Thread(ServerLoop);
			serverThread.Start(); //starts the mjpeg streamer
		}
		else
		{
			handler.debugText.text = "Error starting camera: " + cameraName + " " + res;
			Destroy(this.gameObject);
		}
	}

	public void CloseCamera()
	{
		Destroy(this.gameObject);
	}

	//in tenths of a ms, so 1000 would be 1/10 of a second (highest)
	public void setExposure(int value)
	{
		if (value < 0)
		{
			handler.plugin.Call<int>("SetAutoExposure", cameraName, 8); //auto exposure on (8 auto, 1 manual)
		}
		else
		{
			handler.plugin.Call<int>("SetAutoExposure", cameraName, 1); //auto exposure on (8 auto, 1 manual)
			handler.plugin.Call<int>("SetExposure", cameraName, value); 
		}
	}
	public void expSliderChanged()
	{
		setExposure((int)(exposureSlider.value * 1000));
	}
	public void adjustExposure(int by)
	{
		int current = (int)(exposureSlider.value * 1000);
		int newValue = Math.Clamp(current + by,-1,1000); //todo, should put an autoexposure button
		exposureSlider.value = newValue / 1000.0f; //todo,should allow for longer frame times
		expSliderChanged();
	}

	private void ServerLoop()
	{
		listener = new HttpListener();
		listener.Prefixes.Add($"http://*:{port}/");
		listener.Start();
		
		while (listener.IsListening)
		{
			var context = listener.GetContext();
			AutoResetEvent e = new AutoResetEvent(false);
			events.Add(e);
			Thread t = new Thread((_) => { HandleClient(context, 2, e); }); //todo, allow the min sleep to be set by client
			threads.Add(t);
			
			t.Start();
		}
	}
	
	//note, min sleep refers to, effectively, the max frame rate.  This function will only send new frames, but polls for them
	private void HandleClient(HttpListenerContext context, int minSleepMS, AutoResetEvent waitHandle)
	{
		context.Response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
		context.Response.StatusCode = (int)HttpStatusCode.OK;
		int numSent = 0;
		ServicePointManager.UseNagleAlgorithm = false;
		using (Stream output = context.Response.OutputStream)
		{
			byte[] toSendJpeg = new byte[width*height*3];
			while (started) {
				waitHandle.WaitOne(); //will only work for one client...
				
				int actualBytes = 0;
				lock (jpegLock) //we'll copy the jpeg, which is faster than sending it
				{
					if (cameraJpeg != null)
					{
						Array.Copy(cameraJpeg,toSendJpeg, cameraJpegActualBytes);
						actualBytes = cameraJpegActualBytes;
					}
				}
				if(cameraJpeg == null)
				{
					continue;
				}
				if (toSendJpeg != null)
				{
					numSent++;

					// Write MJPEG frame headers
					string header = $"\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {actualBytes}\r\n\r\n";
					byte[] headerData = Encoding.ASCII.GetBytes(header);
					output.Write(headerData, 0, headerData.Length);
					// Write JPEG frame
					output.Write(toSendJpeg, 0, actualBytes);
					output.Flush();
					
				}
				
				 
			}
		}
		
	}
	private void Update()
	{
		if (opened && started)
		{
			lock (frameLock)
			{
				if (frameData != null)
				{
					cameraTexture.LoadRawTextureData((byte[])(Array)frameData);
					cameraTexture.Apply(false, false);
				}
			}
		}
	}

	private void OnDestroy()
	{
		listener?.Stop();
		serverThread?.Abort();
		handler?.plugin.Call<int>("Close", cameraName);
		handler?.servers.Remove(this.cameraName);
	}

	void OnApplicationQuit()
	{
		listener.Stop();
		serverThread.Abort();
	}

	public void handleJNI()
	{
		if (opened && started)
		{
			//this is called on the primary JNI thread, don't do any unity stuff
			int currFrameNumber = handler.plugin.Call<int>("GetFrameNumber", cameraName);
			if (currFrameNumber != lastFrameNumber)
			{
				lastFrameNumber = currFrameNumber;
				sbyte[] jpegData = handler.plugin.Call<sbyte[]>("GetJpegData", cameraName);


				if (jpegData != null)
				{
					//the first 4 bytes of the data are little endian length
					int actualBytes = BitConverter.ToInt32(new byte[]
					{
						(byte)jpegData[0],
						(byte)jpegData[1],
						(byte)jpegData[2],
						(byte)jpegData[3]
					}, 0);
					//Debug.Log(actualBytes);
					if (cameraJpeg == null)
					{
						cameraJpeg = new byte[width * height * 3]; //big enough
					}
					lock (jpegLock)
					{
						cameraJpegActualBytes = actualBytes;
						Buffer.BlockCopy(jpegData, 4, cameraJpeg, 0, actualBytes);
					}
					foreach (var e in events)
					{
						e.Set();
					}

				}

				lock (frameLock)
				{
					frameData = handler.plugin.Call<sbyte[]>("GetFrameData", cameraName);
				}

			}
		}
	}

}
