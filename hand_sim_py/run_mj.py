import pyximport 
pyximport.install()

from mjremote import mjremote
from mujoco_py import MjSim

import numpy as np 
import time
import random
import mujoco_py
import os

model_dir = 'C:/Users/xieji/.mujoco/mjhaptix150/model/MPL'
model_xml = 'MPL_Boxes.xml'

def main():
    
    xml_path = os.path.join(model_dir, model_xml)
    model = mujoco_py.load_model_from_path(xml_path)
    sim = MjSim(model)
    
    remote = mjremote()
    result = remote.connect()

    t0 = time.time()
    i = 0
    while True:
        # mocap
        grip, pos, quat = remote.getOVRinput()
        sim.data.mocap_pos[:] = pos
        sim.data.mocap_quat[:] = quat
        remote.setmocap(pos, quat)

        # actuation
        sim.data.ctrl[3] = 0.46
        sim.data.ctrl[4] = 0.89
        sim.data.ctrl[5] = 0.37
        sim.data.ctrl[8:11] = grip
        sim.data.ctrl[12] = grip


        # render
        qpos = sim.data.qpos
        remote.setqpos(qpos)

        sim.step()

        i += 1
        if i % 100 == 0:
            t1 = time.time()
            print('FPS: ', 100/(t1-t0))
            t0 = t1


if __name__ == '__main__':
	main()