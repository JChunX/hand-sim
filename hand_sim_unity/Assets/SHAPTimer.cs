using System.Collections;
using System.IO;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;

public class SHAPTimer : MonoBehaviour
{
    public string savefile;
    private bool isDoingTask;
    private float startTime;
    
    // Start is called before the first frame update
    void Start()
    {
        isDoingTask = false;
        try
        {
            if (!File.Exists(savefile))
            {
                File.Create(savefile);
            }
        }
        catch (Exception Ex)
        {
            Debug.Log(Ex.ToString());
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void OnTriggerEnter(Collider other)
    {

        if (!isDoingTask)
        {
            Debug.Log("begin task");
            isDoingTask = true;
            GetComponent<Renderer>().material.color = new Color(0, 1, 1);
            startTime = Time.time;
        }
        else
        {
            WriteLine((Time.time - startTime).ToString());
            GetComponent<Renderer>().material.color = new Color(0, 1, 0.2f);
            isDoingTask = false;
        }

    }

    public async Task WriteLine(String text)
    {
        using (StreamWriter file = new StreamWriter(savefile, append: true))
        {
            await file.WriteLineAsync(text);
        }
    }
}
