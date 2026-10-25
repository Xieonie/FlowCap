"""Mini-SOCKS5-Proxy zum Testen von FlowCaps "Proxy pro Programm".

Start:  python tools\\socks5-testproxy.py            (lauscht auf 127.0.0.1:1080)
Jede Verbindung, die durch den Proxy geht, wird mit Ziel und Datenmenge ausgegeben.
Ohne Anmeldung, nur CONNECT – nicht als echten Proxy verwenden.
"""
import asyncio
import ipaddress
import socket
import struct
import sys
import time

HOST, PORT = "127.0.0.1", int(sys.argv[1]) if len(sys.argv) > 1 else 1080


async def pipe(reader, writer, counter):
    try:
        while data := await reader.read(65536):
            counter[0] += len(data)
            writer.write(data)
            await writer.drain()
    except (ConnectionError, OSError):
        pass
    finally:
        try:
            writer.write_eof()
        except (OSError, RuntimeError):
            pass


async def handle(reader, writer):
    target = "?"
    try:
        ver, n = await reader.readexactly(2)
        await reader.readexactly(n)  # angebotene Anmeldearten
        if ver != 5:
            return
        writer.write(b"\x05\x00")  # keine Anmeldung
        ver, cmd, _, atyp = await reader.readexactly(4)
        if atyp == 1:
            host = str(ipaddress.IPv4Address(await reader.readexactly(4)))
        elif atyp == 4:
            host = str(ipaddress.IPv6Address(await reader.readexactly(16)))
        elif atyp == 3:
            host = (await reader.readexactly((await reader.readexactly(1))[0])).decode()
        else:
            return
        port = struct.unpack("!H", await reader.readexactly(2))[0]
        target = f"{host}:{port}"
        if cmd != 1:
            writer.write(b"\x05\x07\x00\x01" + bytes(6))  # nur CONNECT
            return
        try:
            up_reader, up_writer = await asyncio.wait_for(asyncio.open_connection(host, port), 10)
        except (OSError, asyncio.TimeoutError) as e:
            print(f"{time.strftime('%H:%M:%S')}  FEHLER   {target}  ({e})", flush=True)
            writer.write(b"\x05\x05\x00\x01" + bytes(6))
            return
        writer.write(b"\x05\x00\x00\x01" + socket.inet_aton("0.0.0.0") + b"\x00\x00")
        await writer.drain()
        print(f"{time.strftime('%H:%M:%S')}  VERBUNDEN {target}", flush=True)
        down, up = [0], [0]
        await asyncio.gather(pipe(reader, up_writer, up), pipe(up_reader, writer, down))
        print(f"{time.strftime('%H:%M:%S')}  ENDE      {target}  empfangen {down[0] // 1024} KB, gesendet {up[0] // 1024} KB", flush=True)
    except (asyncio.IncompleteReadError, ConnectionError, OSError):
        pass
    finally:
        writer.close()


async def main():
    server = await asyncio.start_server(handle, HOST, PORT)
    print(f"Test-Proxy läuft auf {HOST}:{PORT} (SOCKS5, ohne Anmeldung). Beenden mit Strg+C.", flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
