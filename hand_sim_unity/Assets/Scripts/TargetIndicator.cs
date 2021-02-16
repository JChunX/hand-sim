using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TargetIndicator : MonoBehaviour
{
    public Vector3 initpos;
    // Start is called before the first frame update
    void Start()
    {
        initpos = gameObject.transform.position;
    }

    // Update is called once per frame
    void Update()
    {
        Vector3 curpos = gameObject.transform.position;
        gameObject.transform.position = new Vector3(curpos.x, initpos.y + Mathf.Sin(2*Time.time) * 0.025f, curpos.z);
        gameObject.transform.rotation = Quaternion.Euler(1, 1, 1) * gameObject.transform.rotation;
    }
}
