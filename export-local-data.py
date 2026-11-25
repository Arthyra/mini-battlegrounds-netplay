"""Extract private runtime data from the local prepared client."""
import argparse
import json
import struct
from functools import lru_cache
from pathlib import Path

import numpy as np
import UnityPy

root = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--client", type=Path, default=root / "client", help="game copy containing Data/")
parser.add_argument("--output", type=Path, default=root, help="output directory for generated JSON files")
args = parser.parse_args()
client = args.client.resolve()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
scene = UnityPy.load(str(client / "Data/cs_scene_102"))
level = next(f for name,f in scene.cabs.items() if name=="buildplayer-cs_scene_102")
transforms = {}
door_objects = {}
shelter_objects = set()
obstacle_ids = {}
movable_groups = {}
for obj in level.objects.values():
    if obj.type.name == "Transform":
        transform = obj.read()
        transforms[transform.m_GameObject.m_PathID] = obj
    if obj.type.name == "MonoBehaviour":
        marker = obj.read()
        script = marker.m_Script.deref().read().m_ClassName
        path_id = marker.m_GameObject.m_PathID
        if script == "DoorMarker":
            door_objects[path_id] = struct.unpack_from('<f', obj.get_raw_data(), 52)[0]
        elif script == "ShelterMarker":
            shelter_objects.add(path_id)
        elif script == "ObstacleMarker":
            obstacle_ids[path_id] = struct.unpack_from("<I", obj.get_raw_data(), 32)[0]
        elif script == "ObstacleGroupMarker":
            # Native DestroyableMarker fields: ID, visual reference, flags, health.
            marker_id, flags, health = struct.unpack_from("<I12xIf", obj.get_raw_data(), 32)
            name = marker.m_GameObject.deref().peek_name()
            if flags & 1 and health > 0:
                movable_groups[path_id] = (health, marker_id)

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
props = []
doors = []

for obj in level.objects.values():
    if obj.type.name != "BoxCollider":
        continue
    box = obj.read()
    if not box.m_Enabled or box.m_IsTrigger:
        continue
    transform = transforms[box.m_GameObject.m_PathID]
    parent = transform.read().m_Father
    parent_id = parent.deref().read().m_GameObject.m_PathID if parent else None
    if parent_id in shelter_objects:
        continue
    mat = world(transform.path_id)
    center = mat @ np.r_[vec(box.m_Center),1]
    extent = abs(mat[:3,:3]) @ (vec(box.m_Size) / 2)
    low, high = center[:3]-extent, center[:3]+extent
    if low[1] > 16.8 or high[1] < 15.45:
        continue
    name=box.m_GameObject.deref().read().m_Name
    if name == "Scene_Size":
        continue
    bounds = [round(float(value), 3) for value in (low[0], low[2], high[0], high[2])]
    if box.m_GameObject.m_PathID in door_objects:
        door_id = box.m_GameObject.m_PathID
        doors.append((obstacle_ids[door_id], door_objects[door_id], bounds))
    elif parent_id in movable_groups:
        health, group_id = movable_groups[parent_id]
        props.append((group_id, health, bounds))
    else:
        boxes.append((name, bounds))
(output / "map102-vehicle-obstacles.json").write_text(
    "[\n" + ",\n".join("  " + json.dumps(bounds) for _, bounds in boxes) + "\n]\n",
    encoding="utf-8",
)

(output / "map102-vehicle-props.json").write_text(json.dumps({"movable": props, "doors": doors}) + "\n", encoding="utf-8")

database = UnityPy.load(str(client / "Data/database"))
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
(output / "skin-catalog.json").write_text(json.dumps(skins) + "\n", encoding="utf-8")
print(f"Exported {len(boxes)} obstacle boxes, {len(props)} movable props and {len(skins)} skin definitions.")
