"""Small token-protected admin/update HTTP server for the LAN room."""
import asyncio
import html
import json
from pathlib import Path
from urllib.parse import parse_qs, urlparse


class AdminServer:
    def __init__(self, match, port, token, root):
        self.match = match
        self.port = port
        self.token = token
        self.root = Path(root)
        self.update_dir = self.root / "update"
        self.server = None

    async def start(self):
        self.server = await asyncio.start_server(self.handle, "0.0.0.0", self.port)
        return self.server

    async def handle(self, reader, writer):
        try:
            raw = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 5)
            lines = raw.decode("latin1").split("\r\n")
            method, target, _ = lines[0].split(" ", 2)
            headers = {}
            for line in lines[1:]:
                if ":" in line:
                    key, value = line.split(":", 1)
                    headers[key.strip().lower()] = value.strip()
            length = int(headers.get("content-length", "0"))
            body = await reader.readexactly(length) if length else b""
            parsed = urlparse(target)
            query = parse_qs(parsed.query)
            token = headers.get("x-admin-token") or (query.get("token", [""])[0])
            path = parsed.path

            if path.startswith("/update/"):
                await self.serve_update(writer, path[len("/update/"):])
                return
            if path == "/" or path == "/index.html":
                await self.send_html(writer, self.page())
                return
            if not self.token or token != self.token:
                await self.send_json(writer, {"ok": False, "error": "unauthorized"}, 401)
                return
            if path == "/api/status":
                await self.send_json(writer, {"ok": True, "status": self.match.admin_status()})
            elif path == "/api/reset":
                self.match.admin_reset()
                await self.send_json(writer, {"ok": True, "status": self.match.admin_status()})
            elif path == "/api/kick":
                values = parse_qs(body.decode("utf-8", "replace")) if body else query
                try:
                    player_id = int((values.get("id") or ["0"])[0])
                except ValueError:
                    player_id = 0
                ok = self.match.admin_kick(player_id)
                await self.send_json(writer, {"ok": ok, "status": self.match.admin_status()})
            elif path == "/api/logs":
                await self.send_text(writer, self.match.admin_logs())
            else:
                await self.send_json(writer, {"ok": False, "error": "not found"}, 404)
        except (asyncio.IncompleteReadError, ConnectionError, ValueError, asyncio.TimeoutError):
            pass
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except ConnectionError:
                pass

    async def serve_update(self, writer, name):
        if not name or "/" in name or "\\" in name or name.startswith("."):
            await self.send_json(writer, {"ok": False, "error": "bad name"}, 400)
            return
        path = self.update_dir / name
        if not path.is_file():
            await self.send_json(writer, {"ok": False, "error": "not found"}, 404)
            return
        content = path.read_bytes()
        content_type = "application/json" if name.endswith(".json") else "application/zip"
        writer.write(
            f"HTTP/1.1 200 OK\r\nContent-Type: {content_type}\r\nContent-Length: {len(content)}\r\nConnection: close\r\n\r\n".encode("ascii")
            + content
        )
        await writer.drain()

    async def send_json(self, writer, value, status=200):
        content = json.dumps(value, ensure_ascii=False).encode("utf-8")
        writer.write(
            f"HTTP/1.1 {status} OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {len(content)}\r\nConnection: close\r\n\r\n".encode("ascii")
            + content
        )
        await writer.drain()

    async def send_text(self, writer, text, status=200):
        content = text.encode("utf-8")
        writer.write(
            f"HTTP/1.1 {status} OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {len(content)}\r\nConnection: close\r\n\r\n".encode("ascii")
            + content
        )
        await writer.drain()

    async def send_html(self, writer, text):
        content = text.encode("utf-8")
        writer.write(
            f"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {len(content)}\r\nConnection: close\r\n\r\n".encode("ascii")
            + content
        )
        await writer.drain()

    def page(self):
        return """<!doctype html><meta charset="utf-8"><title>MBG ECS</title>
<style>body{font:14px system-ui;background:#17191d;color:#e8e8e8;margin:24px}button{padding:6px 10px;margin:2px}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #333;padding:6px;text-align:left}input{padding:6px;width:360px}.muted{color:#999}</style>
<h2>Mini Battlegrounds ECS</h2><p class="muted">管理令牌只保存在浏览器本地。</p>
<p><input id="token" placeholder="admin token"><button onclick="saveToken()">保存令牌</button> <button onclick="load()">刷新</button> <button onclick="reset()">重置房间</button></p>
<div id="status">未加载</div><h3>最近日志</h3><pre id="logs" style="white-space:pre-wrap;background:#111;padding:12px;max-height:420px;overflow:auto"></pre>
<script>
let token=localStorage.getItem('mbg-token')||'';document.getElementById('token').value=token;
function saveToken(){token=document.getElementById('token').value.trim();localStorage.setItem('mbg-token',token);load();}
async function call(path,opt){let r=await fetch(path,{...opt,headers:{'X-Admin-Token':token}});return await r.json();}
async function load(){let r=await call('/api/status');if(!r.ok){document.getElementById('status').textContent='令牌错误或服务不可用';return;}
let s=r.status;let rows=s.players.map(p=>`<tr><td>${p.id}</td><td>${p.name}</td><td>${p.owner?'房主':''}</td><td>${p.ready?'已准备':'未准备'}</td><td>${p.battle?'战斗中':''}${p.disconnected?' 断线中':''}</td><td><button onclick="kick(${p.id})">踢出</button></td></tr>`).join('');
document.getElementById('status').innerHTML=`<p>状态：${s.started?'对局中':'大厅'}，battle ${s.battle_id}，${s.players.length} 人</p><table><tr><th>ID</th><th>昵称</th><th>房主</th><th>准备</th><th>连接</th><th></th></tr>${rows}</table>`;
let l=await fetch('/api/logs',{headers:{'X-Admin-Token':token}});document.getElementById('logs').textContent=await l.text();}
async function reset(){if(confirm('结束当前对局并回到大厅？')){await call('/api/reset',{method:'POST'});load();}}
async function kick(id){if(confirm('踢出玩家 '+id+'？')){await call('/api/kick',{method:'POST',body:'id='+id});load();}}
load();
</script>"""
