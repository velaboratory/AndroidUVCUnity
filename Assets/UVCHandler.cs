
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using System.Threading;

public class UVCHandler : MonoBehaviour
{
    public AndroidJavaObject plugin;
    public MjpegServer serverPrefab;
	string[] cameras;
	public TMP_Dropdown startCameraDropdown;
	public TMP_Text debugText;
	// Start is called before the first frame update
	public Dictionary<string, MjpegServer> servers = new Dictionary<string, MjpegServer>();

	public Transform layoutGroup;
	public Button selectButton;
	int nextPort = 8080;
	Thread JNIThread = null;
	public void SelectClicked()
	{
		string camera = cameras[startCameraDropdown.value];
		lock (servers)
		{
			if (!servers.ContainsKey(camera))
			{
				MjpegServer server = Instantiate(serverPrefab, layoutGroup);
				server.handler = this;
				server.port = nextPort++;
				server.OpenCamera(camera);
				servers.Add(camera, server);
			}
		}
	}
	void Start()
    {
		WebCamDevice[] devices = WebCamTexture.devices; //this useless line of code causes Unity to include camera permissions
		Application.targetFrameRate = 120;
        plugin = new AndroidJavaObject("edu.uga.engr.vel.unityuvcplugin.UnityUVCPlugin");
		plugin.Call("Init");
        cameras = plugin.Call<string[]>("GetUSBDevices");
		debugText.text = string.Join(" ", cameras);
		List<string> dropDownOptions = new List<string>();
		for(int i=0; i < cameras.Length; i++)
		{
			string info = plugin.Call<string>("GetUSBDeviceInfo", cameras[i]);
			string[] parts = info.Split("\n");
			string toAdd = cameras[i];
			
			for(int j=0; j < parts.Length; j++)
			{
				if (parts[j].StartsWith("Product Name:"))
				{
					toAdd = parts[j].Split(":")[1] + toAdd;
				}
			}
			dropDownOptions.Add(toAdd);
		}
		startCameraDropdown.AddOptions(dropDownOptions);
		JNIThread = new Thread(MainThread);
		JNIThread.Priority = System.Threading.ThreadPriority.BelowNormal;
		JNIThread.Start();
	}

	//the plugin can send us messages, but it shouldn't
    void fromPlugin(string message)
    {
        Debug.Log(message);
    }

	void MainThread()
	{
		AndroidJNI.AttachCurrentThread();  //we need this to make JNI calls
		while (true)
		{
			
			lock (servers)
			{
				foreach(var kvp in servers)
				{
					kvp.Value.handleJNI();
				}
			}

			Thread.Sleep(1); //absolute max of 250hz
		}
		

	}
}
