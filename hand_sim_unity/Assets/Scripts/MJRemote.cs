//---------------------------------//
//  This file is part of MuJoCo    //
//  Written by Emo Todorov         //
//  Copyright (C) 2018 Roboti LLC  //
//---------------------------------//

using System;
using System.IO;
using System.Net.Sockets;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using static OVRSkeleton;

public class MJRemote : MonoBehaviour
{

    // socket commands from client
    enum Command : int
    {
        None = 0,               // null command
        GetInput = 1,           // send: key, select, active, refpos[3], refquat[4] (40 bytes)
        GetImage = 2,           // send: rgb image (3*width*height bytes)
        SaveSnapshot = 3,       // (no data exchange)
        SaveVideoframe = 4,     // (no data exchange)
        SetCamera = 5,          // receive: camera index (4 bytes)
        MoveCamera = 6,         // receive: new camera position (12 bytes)
        SetQpos = 7,            // receive: qpos (4*nqpos bytes)
        SetMocap = 8,           // receive: mocap_pos, mocap_quat (28*nmocap bytes)
        GetOVRControllerInput = 9,  // send: Oculus controller data (32 Bytes)
        GetOVRHandInput = 10,        // send: Oculus Hand tracking data (40 Bytes)
        GetOVRControlType = 11
    }


    // prevent repeated instances
    private static MJRemote instance;
    private MJRemote() { }
    public static MJRemote Instance
    {
        get
        {
            if (instance == null)
            {
                instance = new MJRemote();
                return instance;
            }
            else
                throw new System.Exception("MJRemote can only be instantiated once");
        }
    }


    // script options
    public string modelFile = "";
    public string tcpAddress = "127.0.0.1";
    public int tcpPort = 1050;

    // GUI
    static GUIStyle style = null;

    // offscreen rendering
    RenderTexture offrt;
    Texture2D offtex;
    int offwidth = 1280;
    int offheight = 720;
    static int snapshots = 0;
    FileStream videofile = null;

    // data from plugin
    int nqpos = 0;
    int nmocap = 0;
    int ncamera = 0;
    int nobject = 0;
    GameObject[] objects;
    Color selcolor;
    GameObject root = null;
    Camera thecamera = null;
    float[] camfov;

    // remote data
    TcpListener listener = null;
    TcpClient client = null;
    NetworkStream stream = null;
    byte[] buffer;
    int buffersize = 0;
    int camindex = -1;
    float lastcheck = 0;

    // input state
    float lastx = 0;        // updated each frame
    float lasty = 0;        // updated each frame
    float lasttime = 0;     // updated on click
    int lastbutton = 0;     // updated on click
    int lastkey = 0;        // cleared on send

    //Oculus
    float[] ctrlposbuf;
    float[] ctrlquatbuf;
    float[] handctrlbuf;
    Quaternion[] prevbonerot;
    float trigger;
    public GameObject PlayerCamera;
    public GameObject RController;
    public GameObject RHand;
    public GameObject TargetIndicator;
    UnityEngine.Transform CameraTransform;
    OVRHand ROVRHandHandle;
    OVRSkeleton ROVRSkeletonHandle;
    int isUsingController;


    // convert transform from plugin to GameObject
    static unsafe void SetTransform(GameObject obj, MJP.TTransform transform)
    {
        Quaternion q = new Quaternion(0, 0, 0, 1);
        q.SetLookRotation(
            new Vector3(transform.yaxis[0], -transform.yaxis[2], transform.yaxis[1]),
            new Vector3(-transform.zaxis[0], transform.zaxis[2], -transform.zaxis[1])
        );

        obj.transform.localPosition = new Vector3(-transform.position[0], transform.position[2], -transform.position[1]);
        obj.transform.localRotation = q;
        obj.transform.localScale = new Vector3(transform.scale[0], transform.scale[2], transform.scale[1]);
    }


    // convert transform from plugin to Camera
    static unsafe void SetCamera(Camera cam, MJP.TTransform transform)
    {
        Quaternion q = new Quaternion(0, 0, 0, 1);
        q.SetLookRotation(
            new Vector3(transform.zaxis[0], -transform.zaxis[2], transform.zaxis[1]),
            new Vector3(-transform.yaxis[0], transform.yaxis[2], -transform.yaxis[1])
        );

        cam.transform.localPosition = new Vector3(-transform.position[0], transform.position[2], -transform.position[1]);
        cam.transform.localRotation = q;
    }


    // GUI
    private void OnGUI()
    {
        // set style once
        if (style == null)
        {
            style = GUI.skin.textField;
            style.normal.textColor = Color.white;

            // scale font size with DPI
            if (Screen.dpi < 100)
                style.fontSize = 14;
            else if (Screen.dpi > 300)
                style.fontSize = 34;
            else
                style.fontSize = Mathf.RoundToInt(14 + (Screen.dpi - 100.0f) * 0.1f);
        }

        // show connected status
        if (client != null && client.Connected)
            GUILayout.Label("Connected", style);
        else
            GUILayout.Label("Waiting", style);

        // save lastkey
        if (Event.current.isKey)
            lastkey = (int)Event.current.keyCode;
    }


    // initialize
    unsafe void Start()
    {
        // set selection color
        selcolor = new Color(0.5f, 0.5f, 0.5f, 1);

        // initialize plugin
        MJP.Initialize();
        MJP.LoadModel(Application.streamingAssetsPath + "/" + modelFile);

        // get number of renderable objects, allocate map
        MJP.TSize size;
        MJP.GetSize(&size);
        nqpos = size.nqpos;
        nmocap = size.nmocap;
        ncamera = size.ncamera;
        nobject = size.nobject;
        objects = new GameObject[nobject];

        // get root
        root = GameObject.Find("MuJoCo");
        if (root == null)
            throw new System.Exception("MuJoCo root object not found");

        // get camera under root
        int nchild = root.transform.childCount;
        for (int i = 0; i < nchild; i++)
        {
            thecamera = root.transform.GetChild(i).gameObject.GetComponent<Camera>();
            if (thecamera != null)
                break;
        }
        if (thecamera == null)
            throw new System.Exception("No camera found under MuJoCo root object");

        // make map of renderable objects
        for (int i = 0; i < nobject; i++)
        {
            // get object name
            StringBuilder name = new StringBuilder(100);
            MJP.GetObjectName(i, name, 100);

            // find corresponding GameObject
            for (int j = 0; j < nchild; j++)
                if (root.transform.GetChild(j).name == name.ToString())
                {
                    objects[i] = root.transform.GetChild(j).gameObject;
                    break;
                }

            // set initial state
            if (objects[i])
            {
                MJP.TTransform transform;
                int visible;
                int selected;
                MJP.GetObjectState(i, &transform, &visible, &selected);
                SetTransform(objects[i], transform);
                objects[i].SetActive(visible > 0);
            }
        }

        // get camera fov and offscreen resolution
        camfov = new float[ncamera + 1];
        for (int i = -1; i < ncamera; i++)
        {
            MJP.TCamera cam;
            MJP.GetCamera(i, &cam);
            camfov[i + 1] = cam.fov;

            // plugin returns offscreen width and height for all cameras
            offwidth = cam.width;
            offheight = cam.height;
        }

        // prepare offscreen rendering
        offtex = new Texture2D(offwidth, offheight, TextureFormat.RGB24, false);
        offrt = new RenderTexture(offwidth, offheight, 24);
        offrt.width = offwidth;
        offrt.height = offheight;
        offrt.Create();

        // synchronize time
        MJP.SetTime(Time.time);

        // preallocate buffer with maximum possible message size
        buffersize = Math.Max(4, Math.Max(4 * nqpos, 28 * nmocap));
        buffer = new byte[buffersize];

        // start listening for connections
        listener = new TcpListener(System.Net.IPAddress.Parse(tcpAddress), tcpPort);
        listener.Start();

        //Oculus
        ctrlposbuf = new float[3];
        ctrlquatbuf = new float[4];
        trigger = 0;

        handctrlbuf = new float[10];

        CameraTransform = PlayerCamera.transform;

        ROVRHandHandle = RHand.GetComponent<OVRHand>();
        ROVRSkeletonHandle = RHand.GetComponent<OVRSkeleton>();
    }


    // render to texture
    private void RenderToTexture()
    {
        // set to offscreen and render
        thecamera.targetTexture = offrt;
        thecamera.Render();

        // read pixels in regular texure and save
        RenderTexture.active = offrt;
        offtex.ReadPixels(new Rect(0, 0, offwidth, offheight), 0, 0);
        offtex.Apply();

        // restore state
        RenderTexture.active = null;
        thecamera.targetTexture = null;
    }


    // per-frame mouse input; called from Update
    unsafe void ProcessMouse()
    {
        // get modifiers
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        // get button pressed, swap left-right on alt
        int buttonpressed = 0;
        if (Input.GetMouseButton(0))           // left
            buttonpressed = (alt ? 2 : 1);
        if (Input.GetMouseButton(1))           // right
            buttonpressed = (alt ? 1 : 2);
        if (Input.GetMouseButton(2))           // middle
            buttonpressed = 3;

        // get button click, swap left-right on alt
        int buttonclick = 0;
        if (Input.GetMouseButtonDown(0))       // left
            buttonclick = (alt ? 2 : 1);
        if (Input.GetMouseButtonDown(1))       // right
            buttonclick = (alt ? 1 : 2);
        if (Input.GetMouseButtonDown(2))       // middle
            buttonclick = 3;

        // click
        if (buttonclick > 0)
        {
            // set perturbation state
            int newstate = 0;
            if (control)
            {
                // determine new perturbation state
                if (buttonclick == 1)
                    newstate = 2;              // rotate
                else if (buttonclick == 2)
                    newstate = 1;              // move

                // get old perturbation state
                MJP.TPerturb current;
                MJP.GetPerturb(&current);

                // syncronize if starting perturbation now
                if (newstate > 0 && current.active == 0)
                    MJP.PerturbSynchronize();
            }
            MJP.PerturbActive(newstate);

            // process double-click
            if (buttonclick == lastbutton && Time.fixedUnscaledTime - lasttime < 0.25)
            {
                // relative screen position and aspect ratio
                float relx = Input.mousePosition.x / Screen.width;
                float rely = Input.mousePosition.y / Screen.height;
                float aspect = (float)Screen.width / (float)Screen.height;

                // left: select body
                if (buttonclick == 1)
                    MJP.PerturbSelect(relx, rely, aspect);

                // right: set lookat
                else if (buttonclick == 2)
                    MJP.CameraLookAt(relx, rely, aspect);
            }

            // save mouse state
            lastx = Input.mousePosition.x;
            lasty = Input.mousePosition.y;
            lasttime = Time.fixedUnscaledTime;
            lastbutton = buttonclick;
        }

        // left or right drag: manipulate camera or perturb
        if (buttonpressed == 1 || buttonpressed == 2)
        {
            // compute relative displacement and modifier
            float reldx = (Input.mousePosition.x - lastx) / Screen.height;
            float reldy = (Input.mousePosition.y - lasty) / Screen.height;
            int modifier = (shift ? 1 : 0);

            // perturb
            if (control)
            {
                if (buttonpressed == 1)
                    MJP.PerturbRotate(reldx, -reldy, modifier);
                else
                    MJP.PerturbMove(reldx, -reldy, modifier);
            }

            // camera
            else
            {
                if (buttonpressed == 1)
                    MJP.CameraRotate(reldx, -reldy);
                else
                    MJP.CameraMove(reldx, -reldy, modifier);
            }
        }

        // middle drag: zoom camera
        if (buttonpressed == 3)
        {
            float reldy = (Input.mousePosition.y - lasty) / Screen.height;
            MJP.CameraZoom(-reldy);
        }

        // scroll: zoom camera
        if (Input.mouseScrollDelta.y != 0)
            MJP.CameraZoom(-0.05f * Input.mouseScrollDelta.y);

        // save position
        lastx = Input.mousePosition.x;
        lasty = Input.mousePosition.y;

        // release left or right: stop perturb
        if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1))
            MJP.PerturbActive(0);
    }


    // update Unity representation of MuJoCo model
    unsafe private void UpdateModel()
    {
        MJP.TTransform transform;

        // update object states
        for (int i = 0; i < nobject; i++)
            if (objects[i])
            {
                // set transform and visibility
                int visible;
                int selected;
                MJP.GetObjectState(i, &transform, &visible, &selected);
                SetTransform(objects[i], transform);
                objects[i].SetActive(visible > 0);

                // set emission color
                if (selected > 0)
                    objects[i].GetComponent<Renderer>().material.SetColor("_EmissionColor", selcolor);
                else
                    objects[i].GetComponent<Renderer>().material.SetColor("_EmissionColor", Color.black);
            }

        // update camera
        MJP.GetCameraState(camindex, &transform);
        SetCamera(thecamera, transform);
        thecamera.fieldOfView = camfov[camindex + 1];
    }


    // check if connection is still alive
    private bool CheckConnection()
    {
        try
        {
            if (client != null && client.Client != null && client.Client.Connected)
            {
                if (client.Client.Poll(0, SelectMode.SelectRead))
                {
                    if (client.Client.Receive(buffer, SocketFlags.Peek) == 0)
                        return false;
                    else
                        return true;
                }
                else
                    return true;
            }
            else
                return false;
        }
        catch
        {
            return false;
        }
    }


    // read requested number of bytes from socket
    void ReadAll(int n)
    {
        int i = 0;
        while (i < n)
            i += stream.Read(buffer, i, n - i);
    }

    private float swing_twist(Quaternion rotation, Vector3 direction)
    {
        direction = Vector3.Normalize(direction);
        Vector3 rotationAxis = new Vector3(rotation.x, rotation.y, rotation.z);
        float prod = Vector3.Dot(direction, rotationAxis);
        Vector3 proj = prod * direction;
        Quaternion twist = new Quaternion(proj.x, proj.y, proj.z, rotation.w);

        /*if (prod < 0.0f)
        {
            twist.x *= -1;
            twist.y *= -1;
            twist.z *= -1;
            twist.w *= -1;
        }*/

        twist = Quaternion.Normalize(twist);

        return twist.w;
    }


    // per-frame update
    unsafe void Update()
    {
        // mouse interaction
        ProcessMouse();
        UpdateModel();

        // check conection each 0.1 sec
        if (lastcheck + 0.1f < Time.time)
        {
            // broken connection: clear
            if (!CheckConnection())
            {
                client = null;
                stream = null;
            }

            lastcheck = Time.time;
        }

        isUsingController = 0;

        OVRInput.Update();
        OVRInput.Controller activeController = OVRInput.GetActiveController();

        if (activeController == OVRInput.Controller.Touch)
        {
            isUsingController = 1;
            // Get touch controller inputs
            trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            Vector2 lstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            Vector2 rstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);

            CameraTransform.rotation = Quaternion.Euler(new Vector3(CameraTransform.rotation.eulerAngles.x, CameraTransform.rotation.eulerAngles.y + 100f * Time.deltaTime * rstick.x, CameraTransform.rotation.z));
            Vector3 delta_pos = CameraTransform.rotation * (new Vector3(2f * Time.deltaTime * lstick.x, 0f, 2f * Time.deltaTime * lstick.y));
            CameraTransform.position = new Vector3(CameraTransform.position.x, CameraTransform.position.y, CameraTransform.position.z) + delta_pos;

            Vector3 controllerpos = RController.transform.position + CameraTransform.rotation * Vector3.forward * -0.3f;
            Quaternion controllerquat = RController.transform.rotation * Quaternion.Euler(Vector3.left * 180) * Quaternion.Euler(Vector3.forward * 180);

            ctrlposbuf[0] = controllerpos.x;
            ctrlposbuf[1] = controllerpos.z;    // In Mujoco, y is unity's z
            ctrlposbuf[2] = controllerpos.y;
            ctrlquatbuf[0] = -1 * controllerquat.w;
            ctrlquatbuf[1] = controllerquat.x;
            ctrlquatbuf[2] = controllerquat.z;
            ctrlquatbuf[3] = controllerquat.y;
        }

        else if (activeController == OVRInput.Controller.Hands)
        {
            // Track right hand
            SkeletonPoseData data = ((IOVRSkeletonDataProvider) ROVRHandHandle).GetSkeletonPoseData();
            Quaternion[] qdata;
            if (data.IsDataValid)
            {
                int i = 0;
                qdata = new Quaternion [data.BoneRotations.Length];
                if (prevbonerot == null)
                    {
                    prevbonerot = new Quaternion[data.BoneRotations.Length];

                    for (int j = 0; j < prevbonerot.Length; j++)
                    {
                        prevbonerot[j] = new Quaternion(0, 0, 0, 1);
                    }
                }


                foreach (OVRPlugin.Quatf bonerot in data.BoneRotations)
                {
                    qdata[i].x = (prevbonerot[i].x + bonerot.x) / 2;
                    qdata[i].y = (prevbonerot[i].y + bonerot.y) / 2;
                    qdata[i].z = (prevbonerot[i].z + bonerot.z) / 2;
                    qdata[i].w = (prevbonerot[i].w + bonerot.w) / 2;

                    prevbonerot[i].x = bonerot.x;
                    prevbonerot[i].y = bonerot.y;
                    prevbonerot[i].z = bonerot.z;
                    prevbonerot[i].w = bonerot.w;

                    i++;
                }
                Quaternion handquat = RHand.transform.rotation * Quaternion.Euler(Vector3.left * 180) * Quaternion.Euler(Vector3.forward * 180) * Quaternion.Euler(Vector3.up * -87);
                Vector3 handpos = RHand.transform.position + handquat * (Vector3.forward * 0.06f + Vector3.up * -0.01f + Vector3.right * -0.005f);
                ctrlposbuf[0] = handpos.x;
                ctrlposbuf[1] = handpos.z;
                ctrlposbuf[2] = handpos.y;
                ctrlquatbuf[0] = -1f * handquat.w;
                ctrlquatbuf[1] = handquat.x;
                ctrlquatbuf[2] = handquat.z;
                ctrlquatbuf[3] = handquat.y;

                // Maps bone quarternions to joint angles via swing-twist transform

                // Thumb ABD
                handctrlbuf[0] = -5.5f * (((qdata[0] * Quaternion.Inverse(qdata[2])).x) * 2 * Mathf.Acos(1f - (qdata[0] * Quaternion.Inverse(qdata[2])).w) + 1.0f);
                // Thumb MCP
                handctrlbuf[1] = 2.5f * (((qdata[2] * Quaternion.Inverse(qdata[3])).z + (qdata[2] * Quaternion.Inverse(qdata[3])).x) * 2 * Mathf.Acos(1f - (qdata[2] * Quaternion.Inverse(qdata[3])).w) + 0.0f);
                // Thumb PIP
                handctrlbuf[2] = -1.5f * (((qdata[3] * Quaternion.Inverse(qdata[4])).z) * 2 * Mathf.Acos(1f - (qdata[3] * Quaternion.Inverse(qdata[4])).w) - 0.1f);
                // Thumb DIP
                handctrlbuf[3] = -1.2f * (((qdata[4] * Quaternion.Inverse(qdata[5])).z) * 2 * Mathf.Acos(1f - (qdata[4] * Quaternion.Inverse(qdata[5])).w) - 0.2f);
                // Index ABD
                handctrlbuf[4] = -0.9f * ((qdata[0] * Quaternion.Inverse(qdata[6])).y) * 2 * Mathf.Acos(1f - (qdata[0] * Quaternion.Inverse(qdata[6])).w);
                // Index MCP
                handctrlbuf[5] = -Math.Sign((qdata[0] * Quaternion.Inverse(qdata[6])).z) * 2 * Mathf.Acos(swing_twist(qdata[0] * Quaternion.Inverse(qdata[6]), qdata[6] * new Vector3(0, 0, 1)));
                // Middle MCP
                handctrlbuf[6] = -Math.Sign((qdata[0] * Quaternion.Inverse(qdata[9])).z) * 2 * Mathf.Acos(swing_twist(qdata[0] * Quaternion.Inverse(qdata[9]), qdata[9] * new Vector3(0, 0, 1)));
                // Ring MCP
                handctrlbuf[7] = -Math.Sign((qdata[0] * Quaternion.Inverse(qdata[12])).z) * 2 * Mathf.Acos(swing_twist(qdata[0] * Quaternion.Inverse(qdata[12]), qdata[12] * new Vector3(0, 0, 1)));
                // Pinky ABD
                handctrlbuf[8] = 1f * ((qdata[0] * Quaternion.Inverse(qdata[16])).y) * 2 * Mathf.Acos(1f - (qdata[0] * Quaternion.Inverse(qdata[16])).w);
                // Pinky MCP
                handctrlbuf[9] = -Math.Sign((qdata[0] * Quaternion.Inverse(qdata[16])).z) * 2 * Mathf.Acos(swing_twist(qdata[0] * Quaternion.Inverse(qdata[16]), qdata[16] * new Vector3(0, 0, 1)));
            }
        }
        // not connected: accept connection if pending
        if (client == null || !client.Connected)
        {
            if (listener.Pending())
            {
                // make connection
                client = listener.AcceptTcpClient();
                stream = client.GetStream();

                // send 20 bytes: nqpos, nmocap, ncamera, width, height
                stream.Write(BitConverter.GetBytes(nqpos), 0, 4);
                stream.Write(BitConverter.GetBytes(nmocap), 0, 4);
                stream.Write(BitConverter.GetBytes(ncamera), 0, 4);
                stream.Write(BitConverter.GetBytes(offwidth), 0, 4);
                stream.Write(BitConverter.GetBytes(offheight), 0, 4);
            }
        }

        // data available: handle communication
        while (client != null && client.Connected && stream != null && stream.DataAvailable)
        {
            // get command
            ReadAll(4);
            int cmd = BitConverter.ToInt32(buffer, 0);

            // process command
            switch ((Command)cmd)
            {
                // GetInput: send lastkey, select, active, refpos[3], refquat[4]
                case Command.GetInput:
                    MJP.TPerturb perturb;
                    MJP.GetPerturb(&perturb);
                    stream.Write(BitConverter.GetBytes(lastkey), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.select), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.active), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.refpos[0]), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.refpos[1]), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.refpos[2]), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.refquat[0]), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.refquat[1]), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.refquat[2]), 0, 4);
                    stream.Write(BitConverter.GetBytes(perturb.refquat[3]), 0, 4);
                    lastkey = 0;
                    break;

                // GetImage: send 3*width*height bytes
                case Command.GetImage:
                    RenderToTexture();
                    stream.Write(offtex.GetRawTextureData(), 0, 3 * offwidth * offheight);
                    break;

                // SaveSnapshot: no data exchange
                case Command.SaveSnapshot:
                    RenderToTexture();
                    byte[] bytes = offtex.EncodeToPNG();
                    File.WriteAllBytes(Application.streamingAssetsPath + "/../../" + "img_" +
                                       snapshots + ".png", bytes);
                    snapshots++;
                    break;

                // SaveVideoframe: no data exchange
                case Command.SaveVideoframe:
                    if (videofile == null)
                        videofile = new FileStream(Application.streamingAssetsPath + "/../../" + "video.raw",
                                                   FileMode.Create, FileAccess.Write);
                    RenderToTexture();
                    videofile.Write(offtex.GetRawTextureData(), 0, 3 * offwidth * offheight);
                    break;

                // SetCamera: receive camera index
                case Command.SetCamera:
                    ReadAll(4);
                    camindex = BitConverter.ToInt32(buffer, 0);
                    camindex = Math.Max(-1, Math.Min(ncamera - 1, camindex));
                    break;

                // MoveCamera: move player pov
                case Command.MoveCamera:
                    ReadAll(12);
                    fixed (byte* pos = buffer)
                    {
                        float* fpos = (float*)pos;
                        Vector3 newpos = new Vector3(fpos[0], fpos[1], fpos[2]);
                        PlayerCamera.transform.position = new Vector3(newpos[0] - 0.05f, newpos[2] + 0.45f, newpos[1] - 0.5f);
                        TargetIndicator.transform.position = new Vector3(newpos[0], newpos[2] + 0.1f, newpos[1]);
                    }
                    break;

                // SetQpos: receive qpos vector
                case Command.SetQpos:
                    if (nqpos > 0)
                    {
                        ReadAll(4 * nqpos);
                        fixed (byte* qpos = buffer)
                        {
                            MJP.SetQpos((float*)qpos);
                        }
                        MJP.Kinematics();
                        UpdateModel();
                    }
                    break;

                // SetMocap: receive mocap_pos and mocap_quat vectors
                case Command.SetMocap:
                    if (nmocap > 0)
                    {
                        ReadAll(28 * nmocap);
                        fixed (byte* pos = buffer, quat = &buffer[12 * nmocap])
                        {
                            MJP.SetMocap((float*)pos, (float*)quat);
                        }
                        MJP.Kinematics();
                        UpdateModel();
                    }
                    break;

                // GetOVRInput: send Oculus Controller Data (32 Bytes)
                case Command.GetOVRControllerInput:
                    stream.Write(BitConverter.GetBytes(trigger), 0, 4);
                    stream.Write(BitConverter.GetBytes(ctrlposbuf[0]), 0, 4);
                    stream.Write(BitConverter.GetBytes(ctrlposbuf[1]), 0, 4);
                    stream.Write(BitConverter.GetBytes(ctrlposbuf[2]), 0, 4);
                    stream.Write(BitConverter.GetBytes(ctrlquatbuf[0]), 0, 4);
                    stream.Write(BitConverter.GetBytes(ctrlquatbuf[1]), 0, 4);
                    stream.Write(BitConverter.GetBytes(ctrlquatbuf[2]), 0, 4);
                    stream.Write(BitConverter.GetBytes(ctrlquatbuf[3]), 0, 4);
                    break;

                case Command.GetOVRHandInput:
                    foreach (float handctrl in handctrlbuf)
                    {
                        stream.Write(BitConverter.GetBytes(handctrl), 0, 4);
                    }
                    break;

                case Command.GetOVRControlType:
                    stream.Write(BitConverter.GetBytes(isUsingController), 0, 4);
                    break;
            }
        }
    }


    // cleanup
    void OnApplicationQuit()
    {
        // free plugin
        MJP.Close();

        // close tcp listener
        listener.Stop();

        // close file
        if (videofile != null)
            videofile.Close();

        // free render texture
        offrt.Release();
    }
}