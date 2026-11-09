"""Extract private runtime data from the local prepared client."""
import json
from functools import lru_cache
from pathlib import Path

import numpy as np
import UnityPy

root = Path(__file__).resolve().parent
scene = UnityPy.load(str(root / "client/Data/cs_scene_102"))
level = next(f for name,f in scene.cabs.items() if name=="buildplayer-cs_scene_102")
transforms = {}
door_objects = set()
for obj in level.objects.values():
    if obj.type.name == "Transform":
        transform = obj.read()
        transforms[transform.m_GameObject.m_PathID] = obj
    if obj.type.name == "MonoBehaviour":
        script = obj.read().m_Script.deref().read()
        if script.m_ClassName == "DoorMarker":
            door_objects.add(obj.read().m_GameObject.m_PathID)

def vec(value):
    return np.array([value.x, value.y, value.z])

@lru_cache(None)
def world(path_id):
    tr = level.objects[path_id].read()
    q = tr.m_LocalRotation
    x,y,z,w = q.x,q.y,q.z,q.w
    mat = np.eye(4)
    mat[:3,:3] = np.array([
        [1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
        [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
        [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)],
    ]) * vec(tr.m_LocalScale)
    mat[:3,3] = vec(tr.m_LocalPosition)
    return world(tr.m_Father.m_PathID) @ mat if tr.m_Father else mat

boxes = []
for obj in level.objects.values():
    if obj.type.name != "BoxCollider":
        continue
    box = obj.read()
    if not box.m_Enabled or box.m_IsTrigger or box.m_GameObject.m_PathID in door_objects:
        continue
    mat = world(transforms[box.m_GameObject.m_PathID].path_id)
    center = mat @ np.r_[vec(box.m_Center),1]
    extent = abs(mat[:3,:3]) @ (vec(box.m_Size) / 2)
    low, high = center[:3]-extent, center[:3]+extent
    if low[1] > 16.8 or high[1] < 15.45:
        continue
    name=box.m_GameObject.deref().read().m_Name
    if name == "Scene_Size":
        continue
    boxes.append((name,[round(float(value),3) for value in (low[0],low[2],high[0],high[2])]))
(root / "map102-vehicle-obstacles.json").write_text(
    "[\n" + ",\n".join("  " + json.dumps(bounds) for _, bounds in boxes) + "\n]\n",
    encoding="utf-8",
)

database = UnityPy.load(str(root / "client/Data/database"))
skin_text = next(
    obj.read().m_Script for obj in database.objects
    if obj.type.name == "TextAsset" and obj.peek_name() == "SkinData"
)
skins = []
for line in skin_text.splitlines():
    if line.strip():
        fields = line.split("\t")
        skins.append([int(fields[0]), int(fields[3]), int(fields[4])])
assert len({kind for kind, _, _ in skins}) == len(skins)
(root / "skin-catalog.json").write_text(json.dumps(skins) + "\n", encoding="utf-8")
print(f"Exported {len(boxes)} obstacle boxes and {len(skins)} skin definitions.")
