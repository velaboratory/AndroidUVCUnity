using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
public class SimpleExample : MonoBehaviour
{
	AndroidJavaObject plugin;
	void Start()
	{
		WebCamDevice[] devices = WebCamTexture.devices; //this useless line of code causes Unity to include camera permissions
		plugin = new AndroidJavaObject("edu.uga.engr.vel.unityuvcplugin.UnityUVCPlugin"); //declare plugin in the class
		plugin.Call("Init");
		var cameras = plugin.Call<string[]>("GetUSBDevices"); //all UVC devices available
															  //plugin.Call<string>("GetUSBDeviceInfo", cameras[0]); //more info for the camera (optional)
		var cameraName = cameras[0];
		plugin.Call("ObtainPermission", cameraName); //get permission first
		StartCoroutine(RunCamera(cameraName));
	}
	IEnumerator RunCamera(string cameraName)
	{
		while (!plugin.Call<bool>("hasPermission", cameraName))
		{
			yield return null;
		}
		//we have permission, so we open the camera, which returns all resolutions/fps
		var infos = plugin.Call<string[]>("Open", cameraName); //each line has the format (TYPE,RES_X,RES_Y,FPS).  Type will (probably) be 4 (YUV) or 6 (MJPEG).  Currently, the library only supports MJPEG, so filter out the 4s.
															   //now split up infos
		int good_index = -1;
		for(int i=0;i<infos.Length; i++) {
			if (infos[i].StartsWith("6"))
			{
				good_index = i;
				break;
			}
		}
		if(good_index >= 0)
		{
			var info = infos[0].Split(",");
			var width = int.Parse(info[1]);
			var height = int.Parse(info[2]);
			var fps = int.Parse(info[3]);
			var bandwidth = 1.0f; //note if you start more than 2 cameras, you should reduce this.  You may not get the framerate you want, but it won't crash
			var res = plugin.Call<int>("Start", cameraName, width, height, fps, 2, bandwidth);  //at this point, the camera should streaming
			if (res == 0)
			{
				var cameraTexture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
				GetComponent<Renderer>().material.mainTexture = cameraTexture;
				while (true)
				{
					var frameData = plugin.Call<sbyte[]>("GetFrameData", cameraName);
					cameraTexture.LoadRawTextureData((byte[])(Array)frameData);
					cameraTexture.Apply(false, false);
					yield return null;
				}
			}
		}
	}
}
