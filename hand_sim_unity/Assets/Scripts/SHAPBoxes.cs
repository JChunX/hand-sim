using System.Collections;
using System.IO;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;

public class SHAPBoxes : MonoBehaviour
{
    private bool isDoingTask;
    private float startTime;

    // Start is called before the first frame update
    void Start()
    {
    }

    // Update is called once per frame
    void Update()
    {
        if (isDoingTask)
        {
            if ((Time.time - startTime) > 60f)
            {
                Debug.Log("Times up");
                isDoingTask = false;
                GetComponent<Renderer>().material.color = new Color(0, 1, 0.2f);
            }
        }
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
    }
}
