using System.Collections;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;

public class SHAPTimer : MonoBehaviour
{
    public string savepath;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void OnCollisionEnter(Collision collision)
    {
        Debug.Log("collisionevent");
    }
}
