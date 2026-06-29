from __future__ import annotations

import ipaddress
import json
import os
import posixpath
import queue
import re
import socket
import subprocess
import threading
import time
import traceback
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path
from stat import S_ISDIR
from tkinter import (
    BOTH,
    DISABLED,
    END,
    EW,
    LEFT,
    NORMAL,
    RIGHT,
    VERTICAL,
    W,
    X,
    Y,
    Button,
    Entry,
    Frame,
    Label,
    LabelFrame,
    Listbox,
    StringVar,
    Tk,
    filedialog,
    messagebox,
    ttk,
)

try:
    import paramiko
except ImportError:  # pragma: no cover - handled in the GUI at runtime
    paramiko = None

try:
    import keyring
except ImportError:  # pragma: no cover - credentials fall back to username/key metadata only
    keyring = None

try:
    from zeroconf import ServiceBrowser, ServiceListener, Zeroconf
except ImportError:  # pragma: no cover - discovery falls back to scanning
    ServiceBrowser = None
    ServiceListener = object
    Zeroconf = None


APP_TITLE = "FileTransferHelper"
SSH_PORT = 22
TRANSFER_RETRY_ATTEMPTS = 12
TRANSFER_RETRY_DELAY_SECONDS = 5
DISCOVERY_LOG_PATH = Path(__file__).with_name("discovery.log")
TRANSFER_LOG_PATH = Path(__file__).with_name("transfer.log")
DEVICE_CACHE_PATH = Path(__file__).with_name("devices.json")
LOG_LOCK = threading.Lock()


def write_log(path: Path, message: str) -> None:
    timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
    line = f"[{timestamp}] {message}\n" if message else "\n"
    with LOG_LOCK:
        with path.open("a", encoding="utf-8") as handle:
            handle.write(line)


@dataclass(frozen=True)
class HostCandidate:
    name: str
    address: str
    source: str
    username: str = ""
    use_key: bool = False
    key_path: str = ""

    @property
    def label(self) -> str:
        return f"{self.name} ({self.address})"


@dataclass(frozen=True)
class TransferItem:
    local_path: Path
    remote_path: str
    action: str
    display_name: str


@dataclass(frozen=True)
class RemoteEntry:
    name: str
    is_dir: bool
    size: int = 0

    @property
    def display_name(self) -> str:
        return f"{self.name}/" if self.is_dir else self.name


class _MdnsListener(ServiceListener):
    def __init__(self, service_source: str) -> None:
        self.hosts: list[HostCandidate] = []
        self.service_source = service_source

    def add_service(self, zeroconf: Zeroconf, service_type: str, name: str) -> None:
        info = zeroconf.get_service_info(service_type, name, timeout=1000)
        if not info:
            return

        service_name = name.replace(f".{service_type}", "").rstrip(".")
        display_name = NetworkDiscovery._best_mdns_display_name(service_name, info.server)
        for raw_address in info.addresses:
            if len(raw_address) != 4:
                continue
            address = socket.inet_ntoa(raw_address)
            self.hosts.append(HostCandidate(display_name, address, self.service_source))

    def update_service(self, zeroconf: Zeroconf, service_type: str, name: str) -> None:
        self.add_service(zeroconf, service_type, name)

    def remove_service(self, zeroconf: Zeroconf, service_type: str, name: str) -> None:
        return


class NetworkDiscovery:
    _log_lock = threading.Lock()

    @staticmethod
    def cached_hosts() -> list[HostCandidate]:
        try:
            raw_hosts = json.loads(DEVICE_CACHE_PATH.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return []

        hosts: list[HostCandidate] = []
        for item in raw_hosts:
            if not isinstance(item, dict):
                continue
            name = str(item.get("name", "")).strip()
            address = str(item.get("address", "")).strip()
            source = str(item.get("source", "sparad")).strip() or "sparad"
            username = str(item.get("username", "")).strip()
            use_key = bool(item.get("use_key", False))
            key_path = str(item.get("key_path", "")).strip()
            if name and address:
                hosts.append(HostCandidate(name, address, source, username, use_key, key_path))
        return NetworkDiscovery._sort_hosts(hosts)

    @staticmethod
    def save_cache(hosts: list[HostCandidate]) -> None:
        unique: dict[str, HostCandidate] = {}
        for host in hosts:
            existing = unique.get(host.address)
            if existing is None or NetworkDiscovery._candidate_quality(host) > NetworkDiscovery._candidate_quality(existing):
                unique[host.address] = host

        data = [
            {
                "name": host.name,
                "address": host.address,
                "source": host.source,
                "username": host.username,
                "use_key": host.use_key,
                "key_path": host.key_path,
                "updated_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            }
            for host in NetworkDiscovery._sort_hosts(list(unique.values()))
        ]
        DEVICE_CACHE_PATH.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")

    @staticmethod
    def quick_verify_cached(progress: callable | None = None) -> list[HostCandidate]:
        cached = NetworkDiscovery.cached_hosts()
        if not cached:
            return []

        if progress:
            progress("Kontrollerar sparade enheter...")
        NetworkDiscovery._log("")
        NetworkDiscovery._log("=== Snabbkontroll av sparade enheter ===")

        reachable: list[HostCandidate] = []
        with ThreadPoolExecutor(max_workers=min(16, max(1, len(cached)))) as executor:
            futures = {executor.submit(NetworkDiscovery._port_is_open, host.address, SSH_PORT): host for host in cached}
            for future in as_completed(futures):
                host = futures[future]
                try:
                    is_open = future.result()
                except Exception:
                    is_open = False
                if is_open:
                    refreshed = NetworkDiscovery._probe_ssh(host.address)
                    if refreshed and NetworkDiscovery._candidate_quality(refreshed) > NetworkDiscovery._candidate_quality(host):
                        refreshed = NetworkDiscovery._merge_host_metadata(refreshed, host)
                        reachable.append(refreshed)
                        NetworkDiscovery._log(f"Sparad enhet svarar med uppdaterat namn: {refreshed.label}")
                    else:
                        reachable.append(host)
                        NetworkDiscovery._log(f"Sparad enhet svarar: {host.label}")
                else:
                    NetworkDiscovery._log(f"Sparad enhet svarar inte: {host.label}")
        return NetworkDiscovery._sort_hosts(reachable)

    @staticmethod
    def discover(progress: callable | None = None, use_cached_first: bool = True) -> list[HostCandidate]:
        found: dict[str, HostCandidate] = {}
        NetworkDiscovery._log("")
        NetworkDiscovery._log("=== Ny nätverkssökning ===")

        def add(candidate: HostCandidate) -> None:
            existing = found.get(candidate.address)
            if existing:
                if NetworkDiscovery._candidate_quality(candidate) > NetworkDiscovery._candidate_quality(existing):
                    candidate = NetworkDiscovery._merge_host_metadata(candidate, existing)
                    found[candidate.address] = candidate
                    NetworkDiscovery._log(
                        f"Ersätter {existing.label} [{existing.source}] med {candidate.label} [{candidate.source}]"
                    )
                else:
                    found[candidate.address] = NetworkDiscovery._merge_host_metadata(existing, candidate)
                    NetworkDiscovery._log(
                        f"Behåller {existing.label} [{existing.source}]; "
                        f"ignorerar dubblett {candidate.label} [{candidate.source}]"
                    )
                return
            found[candidate.address] = candidate
            NetworkDiscovery._log(f"Lade till: {candidate.label} [{candidate.source}]")

        if use_cached_first:
            for candidate in NetworkDiscovery.quick_verify_cached(progress):
                add(candidate)

        for candidate in NetworkDiscovery._known_raspberry_names():
            add(candidate)

        if progress:
            progress("Söker via mDNS...")
        for candidate in NetworkDiscovery._mdns_hosts():
            add(candidate)

        if progress:
            progress("Skannar lokala nätverket efter SSH...")
        for candidate in NetworkDiscovery._scan_local_networks():
            add(candidate)

        results = NetworkDiscovery._sort_hosts(list(found.values()))
        if results:
            NetworkDiscovery.save_cache(results)
        NetworkDiscovery._log(f"Slutresultat: {len(results)} enhet(er)")
        for candidate in results:
            NetworkDiscovery._log(f"  {candidate.label} [{candidate.source}]")
        return results

    @staticmethod
    def _log(message: str) -> None:
        write_log(DISCOVERY_LOG_PATH, message)

    @staticmethod
    def _known_raspberry_names() -> list[HostCandidate]:
        candidates: list[HostCandidate] = []
        hostname_guesses = (
            ("raspberrypi.local", "raspberrypi.local"),
            ("raspberrypi", "raspberrypi"),
            ("pi.local", "pi.local"),
            ("piserver.local", "PISERVER"),
            ("piserver", "PISERVER"),
            ("pi-tv.local", "Pi TV"),
            ("pi-tv", "Pi TV"),
            ("pitv.local", "Pi TV"),
            ("pitv", "Pi TV"),
        )
        for hostname, display_name in hostname_guesses:
            try:
                address = socket.gethostbyname(hostname)
            except OSError:
                NetworkDiscovery._log(f"Standardnamn saknas: {hostname}")
                continue
            if NetworkDiscovery._port_is_open(address, SSH_PORT):
                NetworkDiscovery._log(f"Standardnamn fungerar: {hostname} -> {address}")
                candidates.append(HostCandidate(display_name, address, "standardnamn"))
        return candidates

    @staticmethod
    def _mdns_hosts() -> list[HostCandidate]:
        if Zeroconf is None or ServiceBrowser is None:
            NetworkDiscovery._log("mDNS hoppas över: zeroconf är inte installerat")
            return []

        hosts: list[HostCandidate] = []
        for service_type, source in (
            ("_ssh._tcp.local.", "mDNS SSH"),
            ("_sftp-ssh._tcp.local.", "mDNS SFTP"),
            ("_workstation._tcp.local.", "mDNS workstation"),
        ):
            hosts.extend(NetworkDiscovery._mdns_service_hosts(service_type, source))
        return hosts

    @staticmethod
    def _mdns_service_hosts(service_type: str, source: str) -> list[HostCandidate]:
        zeroconf = Zeroconf()
        listener = _MdnsListener(source)
        try:
            ServiceBrowser(zeroconf, service_type, listener)
            time.sleep(1.6)
        finally:
            zeroconf.close()
        NetworkDiscovery._log(f"{source} hittade {len(listener.hosts)} tjänst(er)")
        return listener.hosts

    @staticmethod
    def _best_mdns_display_name(service_name: str, server_name: str | None) -> str:
        cleaned_service = NetworkDiscovery._clean_hostname(service_name)
        cleaned_server = NetworkDiscovery._clean_hostname(server_name)

        if cleaned_service and not cleaned_service.lower().startswith(("ssh ", "sftp ")):
            return cleaned_service
        return cleaned_server or cleaned_service or "mDNS-enhet"

    @staticmethod
    def _scan_local_networks() -> list[HostCandidate]:
        addresses = NetworkDiscovery._candidate_scan_addresses()
        if not addresses:
            NetworkDiscovery._log("Ingen lokal nätverksadress kunde skannas")
            return []

        NetworkDiscovery._log(f"Skannar {len(addresses)} adress(er) efter SSH på port 22")
        hosts: list[HostCandidate] = []
        with ThreadPoolExecutor(max_workers=96) as executor:
            futures = {executor.submit(NetworkDiscovery._probe_ssh, address): address for address in addresses}
            for future in as_completed(futures):
                candidate = future.result()
                if candidate:
                    hosts.append(candidate)
        return hosts

    @staticmethod
    def _candidate_scan_addresses() -> list[str]:
        own_addresses, networks = NetworkDiscovery._windows_ipv4_networks()
        if not networks:
            own = NetworkDiscovery._primary_ipv4()
            if own:
                network = ipaddress.ip_network(f"{own}/24", strict=False)
                networks = [network]
                own_addresses = {own}

        results: list[str] = []
        seen: set[str] = set()
        for network in networks:
            scan_network = network
            if network.num_addresses > 256:
                own = next((ip for ip in own_addresses if ipaddress.ip_address(ip) in network), None)
                if own:
                    scan_network = ipaddress.ip_network(f"{own}/24", strict=False)
                else:
                    continue

            for address in scan_network.hosts():
                value = str(address)
                if value in own_addresses or value in seen:
                    continue
                seen.add(value)
                results.append(value)
        return results

    @staticmethod
    def _windows_ipv4_networks() -> tuple[set[str], list[ipaddress.IPv4Network]]:
        try:
            output = subprocess.check_output(
                ["ipconfig"],
                text=True,
                encoding="utf-8",
                errors="ignore",
                timeout=4,
            )
        except (OSError, subprocess.SubprocessError):
            return set(), []

        own_addresses: set[str] = set()
        networks: list[ipaddress.IPv4Network] = []
        current_ip: str | None = None

        for line in output.splitlines():
            if "IPv4" in line:
                match = re.search(r"(\d+\.\d+\.\d+\.\d+)", line)
                current_ip = match.group(1) if match else None
                if current_ip and not current_ip.startswith(("127.", "169.254.")):
                    own_addresses.add(current_ip)
                continue

            if current_ip and ("Subnet Mask" in line or "Nätmask" in line):
                match = re.search(r"(\d+\.\d+\.\d+\.\d+)", line)
                if match:
                    try:
                        networks.append(ipaddress.ip_network(f"{current_ip}/{match.group(1)}", strict=False))
                    except ValueError:
                        pass
                current_ip = None

        return own_addresses, networks

    @staticmethod
    def _primary_ipv4() -> str | None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            sock.connect(("8.8.8.8", 80))
            return sock.getsockname()[0]
        except OSError:
            return None
        finally:
            sock.close()

    @staticmethod
    def _probe_ssh(address: str) -> HostCandidate | None:
        try:
            with socket.create_connection((address, SSH_PORT), timeout=0.35) as sock:
                sock.settimeout(0.35)
                try:
                    banner = sock.recv(80).decode("ascii", errors="ignore").strip()
                except OSError:
                    banner = ""
        except OSError:
            return None

        fallback_name = "Raspberry Pi" if "raspberry" in banner.lower() else "SSH-enhet"
        NetworkDiscovery._log(f"SSH hittad: {address}, banner={banner or '<ingen banner>'}")
        name, source = NetworkDiscovery._friendly_name_for_address(address, fallback_name)
        NetworkDiscovery._log(f"Namnval för {address}: {name} ({source})")
        return HostCandidate(name, address, source)

    @staticmethod
    def _friendly_name_for_address(address: str, fallback_name: str) -> tuple[str, str]:
        for resolver in (
            NetworkDiscovery._reverse_dns_name,
            NetworkDiscovery._ping_name,
            NetworkDiscovery._netbios_name,
        ):
            name = resolver(address)
            if name:
                NetworkDiscovery._log(f"{resolver.__name__} för {address}: {name}")
                return name, "port 22 + namn"
            NetworkDiscovery._log(f"{resolver.__name__} för {address}: ingen träff")
        return fallback_name, "port 22"

    @staticmethod
    def _reverse_dns_name(address: str) -> str | None:
        try:
            hostname, _, _ = socket.gethostbyaddr(address)
        except OSError:
            return None
        return NetworkDiscovery._clean_hostname(hostname)

    @staticmethod
    def _ping_name(address: str) -> str | None:
        try:
            output = subprocess.check_output(
                ["ping", "-a", "-n", "1", "-w", "500", address],
                text=True,
                encoding="utf-8",
                errors="ignore",
                timeout=1.5,
            )
        except (OSError, subprocess.SubprocessError):
            return None

        first_line = next((line.strip() for line in output.splitlines() if line.strip()), "")
        match = re.search(r"\b([A-Za-z0-9][A-Za-z0-9_.-]+)\s+\[" + re.escape(address) + r"\]", first_line)
        return NetworkDiscovery._clean_hostname(match.group(1)) if match else None

    @staticmethod
    def _netbios_name(address: str) -> str | None:
        try:
            output = subprocess.check_output(
                ["nbtstat", "-A", address],
                text=True,
                encoding="utf-8",
                errors="ignore",
                timeout=2,
            )
        except (OSError, subprocess.SubprocessError):
            return None

        ignored_names = {"WORKGROUP", "MSHOME", "__MSBROWSE__"}
        for line in output.splitlines():
            match = re.match(r"\s*([A-Za-z0-9_-]{1,15})\s+<00>\s+UNIQUE", line, re.IGNORECASE)
            if not match:
                continue
            name = match.group(1).strip()
            if name.upper() not in ignored_names:
                return NetworkDiscovery._clean_hostname(name)
        return None

    @staticmethod
    def _clean_hostname(hostname: str | None) -> str | None:
        if not hostname:
            return None
        hostname = hostname.strip().rstrip(".")
        if not hostname or hostname == "localhost":
            return None
        return hostname

    @staticmethod
    def _port_is_open(address: str, port: int) -> bool:
        try:
            with socket.create_connection((address, port), timeout=0.6):
                return True
        except OSError:
            return False

    @staticmethod
    def _candidate_quality(candidate: HostCandidate) -> int:
        generic_names = {"ssh-enhet", "raspberry pi", "mdns-enhet"}
        score = 0
        if candidate.name.strip().lower() not in generic_names:
            score += 10
        if candidate.source.startswith("mDNS"):
            score += 3
        if candidate.source == "standardnamn":
            score += 2
        if "namn" in candidate.source:
            score += 1
        return score

    @staticmethod
    def _merge_host_metadata(primary: HostCandidate, fallback: HostCandidate) -> HostCandidate:
        return HostCandidate(
            primary.name,
            primary.address,
            primary.source,
            primary.username or fallback.username,
            primary.use_key or fallback.use_key,
            primary.key_path or fallback.key_path,
        )

    @staticmethod
    def _sort_hosts(hosts: list[HostCandidate]) -> list[HostCandidate]:
        return sorted(hosts, key=lambda item: (not NetworkDiscovery._looks_like_pi(item), item.name.lower(), item.address))

    @staticmethod
    def _looks_like_pi(candidate: HostCandidate) -> bool:
        text = f"{candidate.name} {candidate.source}".lower()
        return "raspberry" in text or "pi" in text


class CredentialStore:
    @staticmethod
    def password_for(address: str) -> str:
        if keyring is None or not address:
            return ""
        try:
            return keyring.get_password(APP_TITLE, address) or ""
        except Exception as exc:
            write_log(TRANSFER_LOG_PATH, f"Kunde inte läsa lösenord från Credential Manager för {address}: {exc}")
            return ""

    @staticmethod
    def save_password(address: str, password: str) -> None:
        if keyring is None or not address or not password:
            return
        try:
            keyring.set_password(APP_TITLE, address, password)
        except Exception as exc:
            write_log(TRANSFER_LOG_PATH, f"Kunde inte spara lösenord i Credential Manager för {address}: {exc}")


class SftpTransferClient:
    def __init__(self) -> None:
        self.client = None
        self.sftp = None
        self.connection_params: dict[str, str | None] = {}

    def connect(self, host: str, username: str, password: str | None, key_path: str | None) -> str:
        if paramiko is None:
            raise RuntimeError("Paramiko saknas. Kör .\\run.ps1 så installeras beroenden automatiskt.")

        self.connection_params = {
            "host": host,
            "username": username,
            "password": password,
            "key_path": key_path,
        }
        self._connect_current()
        return self.normalize(".")

    def _connect_current(self) -> None:
        if paramiko is None:
            raise RuntimeError("Paramiko saknas. Kör .\\run.ps1 så installeras beroenden automatiskt.")

        host = self.connection_params.get("host")
        username = self.connection_params.get("username")
        password = self.connection_params.get("password")
        key_path = self.connection_params.get("key_path")
        if not host or not username:
            raise RuntimeError("Saknar sparade anslutningsuppgifter för återanslutning.")

        self.close()
        client = paramiko.SSHClient()
        client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        connect_kwargs = {
            "hostname": str(host),
            "port": SSH_PORT,
            "username": str(username),
            "timeout": 8,
            "banner_timeout": 8,
            "auth_timeout": 12,
            "look_for_keys": not key_path and not password,
        }
        if password:
            connect_kwargs["password"] = password
        if key_path:
            connect_kwargs["key_filename"] = key_path

        client.connect(**connect_kwargs)
        self.client = client
        self.sftp = client.open_sftp()

    def close(self) -> None:
        if self.sftp:
            self.sftp.close()
        if self.client:
            self.client.close()
        self.sftp = None
        self.client = None

    def normalize(self, path: str) -> str:
        self._require_sftp()
        return self.sftp.normalize(path)

    def list_entries(self, path: str) -> list[RemoteEntry]:
        self._require_sftp()
        entries: list[RemoteEntry] = []
        try:
            remote_items = self.sftp.listdir_attr(path)
        except OSError as exc:
            self._log_exception(f"Kunde inte lista fjärrmapp: {path}", exc)
            raise

        for item in remote_items:
            if S_ISDIR(item.st_mode):
                entries.append(RemoteEntry(item.filename, True, int(item.st_size)))
            else:
                entries.append(RemoteEntry(item.filename, False, int(item.st_size)))

        direct_file_count = sum(1 for entry in entries if not entry.is_dir)
        for item in remote_items:
            if not S_ISDIR(item.st_mode):
                continue
            directory_path = posixpath.join(path, item.filename)
            for remote_file, size in self._list_files_under(directory_path, item.filename):
                entries.append(RemoteEntry(remote_file, False, size))

        nested_file_count = sum(1 for entry in entries if not entry.is_dir) - direct_file_count
        self._log(
            f"Listade fjärrmapp {path}: {len(remote_items)} direkt post(er), "
            f"{nested_file_count} fil(er) i undermappar"
        )
        return sorted(entries, key=lambda item: (not item.is_dir, item.name.lower()))

    def _list_files_under(self, remote_dir: str, relative_dir: str) -> list[tuple[str, int]]:
        files: list[tuple[str, int]] = []
        try:
            entries = self.sftp.listdir_attr(remote_dir)
        except OSError as exc:
            self._log(f"Kunde inte läsa undermapp {remote_dir}: {self._error_text(exc)}")
            return files

        for entry in entries:
            remote_path = posixpath.join(remote_dir, entry.filename)
            relative_path = posixpath.join(relative_dir, entry.filename)
            if S_ISDIR(entry.st_mode):
                files.extend(self._list_files_under(remote_path, relative_path))
            else:
                files.append((relative_path, int(entry.st_size)))
        return files

    def mkdir(self, path: str) -> None:
        self._require_sftp()
        self._mkdir_p(path)

    def find_duplicate_files(self, remote_root: str) -> list[str]:
        self._require_sftp()
        remote_root = self.normalize(remote_root)
        remote_files = self._index_remote_tree(remote_root)
        duplicates: list[str] = []

        for remote_path, info in remote_files.items():
            directory = posixpath.dirname(remote_path)
            filename = posixpath.basename(remote_path)
            stem, extension = posixpath.splitext(filename)
            match = re.match(r"^(.+) \(([1-9]\d*)\)$", stem)
            if not match:
                continue

            original_path = posixpath.join(directory, f"{match.group(1)}{extension}")
            original_info = remote_files.get(original_path)
            if original_info and int(original_info["size"]) == int(info["size"]):
                duplicates.append(remote_path)

        self._log(f"Hittade {len(duplicates)} dubblettfil(er) under {remote_root}")
        return sorted(duplicates, key=str.lower)

    def delete_files(self, remote_files: list[str]) -> tuple[list[str], list[tuple[str, str]]]:
        self._require_sftp()
        deleted: list[str] = []
        failed: list[tuple[str, str]] = []

        for remote_file in remote_files:
            try:
                self.sftp.remove(remote_file)
                deleted.append(remote_file)
                self._log(f"Tog bort dubblettfil: {remote_file}")
            except Exception as exc:
                error = self._error_text(exc)
                failed.append((remote_file, error))
                self._log(f"Kunde inte ta bort dubblettfil {remote_file}: {error}")

        return deleted, failed

    def upload_paths(self, local_paths: list[Path], remote_destination: str, progress: callable, status: callable | None = None) -> None:
        self._require_sftp()
        self._log("")
        self._log("=== Ny överföring ===")
        self._log(f"Destination från GUI: {remote_destination}")
        for path in local_paths:
            self._log(f"Lokalt val: {path}")

        try:
            remote_destination = self.normalize(remote_destination)
            self._log(f"Normaliserad destination: {remote_destination}")
            self._mkdir_p(remote_destination)
        except Exception as exc:
            self._log_exception("Kunde inte förbereda destination", exc)
            raise RuntimeError(
                f"Kunde inte förbereda destinationen {remote_destination}: {self._error_text(exc)}"
            ) from exc

        self._log("Planerar överföring genom att läsa fjärrmappar först...")
        if status:
            status("Kontrollerar vilka filer som redan finns på Raspberry Pi...")
        plan = self._build_transfer_plan(local_paths, remote_destination)
        total_files = len(plan)
        skipped = sum(1 for item in plan if item.action == "skipped")
        renamed = sum(1 for item in plan if item.action == "renamed")
        uploads = sum(1 for item in plan if item.action in {"uploaded", "renamed"})
        self._log(f"Plan klar. Totalt={total_files}, skicka={uploads}, hoppa över={skipped}, döp om={renamed}")

        done = 0
        for item in plan:
            if item.action == "skipped":
                self._log(f"Hoppar över befintlig fil med samma storlek: {item.local_path} -> {item.remote_path}")
                done += 1
                progress(done, total_files, item.display_name, item.action)
                continue

            self._upload_planned_file(item, status)
            done += 1
            progress(done, total_files, item.display_name, item.action)
        self._log(f"Överföring klar. Behandlade={done}, hoppade över={skipped}, döpte om={renamed}")

    def _build_transfer_plan(self, local_paths: list[Path], remote_destination: str) -> list[TransferItem]:
        plan: list[TransferItem] = []
        remote_cache: dict[str, dict[str, object]] = {}

        for local_path in local_paths:
            if local_path.is_dir():
                remote_root = posixpath.join(remote_destination, local_path.name)
                remote_cache.update(self._index_remote_tree(remote_root))
                for root, _, files in os.walk(local_path):
                    root_path = Path(root)
                    relative_root = root_path.relative_to(local_path)
                    remote_dir = remote_root if str(relative_root) == "." else posixpath.join(
                        remote_root,
                        *relative_root.parts,
                    )
                    for filename in files:
                        child = root_path / filename
                        remote_file = posixpath.join(remote_dir, filename)
                        plan.append(self._planned_item(child, remote_file, remote_cache))
            else:
                remote_cache.update(self._index_remote_directory(remote_destination))
                remote_file = posixpath.join(remote_destination, local_path.name)
                plan.append(self._planned_item(local_path, remote_file, remote_cache))

        return plan

    def _planned_item(self, local_path: Path, remote_file: str, remote_cache: dict[str, dict[str, object]]) -> TransferItem:
        local_size = int(local_path.stat().st_size)
        remote_info = remote_cache.get(remote_file)
        if remote_info and int(remote_info["size"]) == local_size:
            return TransferItem(local_path, remote_file, "skipped", local_path.name)
        if remote_info:
            unique_path = self._unique_remote_path(remote_file, remote_cache)
            return TransferItem(local_path, unique_path, "renamed", local_path.name)
        return TransferItem(local_path, remote_file, "uploaded", local_path.name)

    def _upload_planned_file(self, item: TransferItem, status: callable | None = None) -> None:
        self._mkdir_p(posixpath.dirname(item.remote_path))
        if item.action == "renamed":
            self._log(
                f"Befintlig fil skiljer sig; skriver inte över. "
                f"Skickar som nytt namn: {item.local_path} -> {item.remote_path}"
            )
        else:
            self._log(f"Skickar fil: {item.local_path} -> {item.remote_path}")

        for attempt in range(1, TRANSFER_RETRY_ATTEMPTS + 1):
            try:
                self.sftp.put(str(item.local_path), item.remote_path)
                self.sftp.utime(item.remote_path, (int(item.local_path.stat().st_atime), int(item.local_path.stat().st_mtime)))
                return
            except Exception as exc:
                if not self._is_connection_error(exc) or attempt >= TRANSFER_RETRY_ATTEMPTS:
                    self._log_exception(f"Misslyckades med fil: {item.local_path} -> {item.remote_path}", exc)
                    raise RuntimeError(
                        f"Kunde inte skicka {item.local_path.name} till {item.remote_path}: {self._error_text(exc)}"
                    ) from exc

                self._log_exception(
                    f"Anslutningen bröts under fil: {item.local_path} -> {item.remote_path}. "
                    f"Försök {attempt}/{TRANSFER_RETRY_ATTEMPTS}",
                    exc,
                )
                if status:
                    status(
                        f"Anslutningen bröts. Väntar {TRANSFER_RETRY_DELAY_SECONDS}s och återansluter "
                        f"({attempt}/{TRANSFER_RETRY_ATTEMPTS})..."
                    )
                time.sleep(TRANSFER_RETRY_DELAY_SECONDS)
                self._reconnect(status)

    def _index_remote_tree(self, remote_root: str) -> dict[str, dict[str, object]]:
        index: dict[str, dict[str, object]] = {}
        self._index_remote_tree_into(remote_root, index)
        self._log(f"Indexerade {len(index)} befintliga fjärrfiler under {remote_root}")
        return index

    def _index_remote_tree_into(self, remote_dir: str, index: dict[str, dict[str, object]]) -> None:
        try:
            entries = self.sftp.listdir_attr(remote_dir)
        except OSError:
            return

        for entry in entries:
            remote_path = posixpath.join(remote_dir, entry.filename)
            if S_ISDIR(entry.st_mode):
                self._index_remote_tree_into(remote_path, index)
            else:
                index[remote_path] = {"size": int(entry.st_size), "mtime": int(entry.st_mtime)}

    def _index_remote_directory(self, remote_dir: str) -> dict[str, dict[str, object]]:
        index: dict[str, dict[str, object]] = {}
        try:
            entries = self.sftp.listdir_attr(remote_dir)
        except OSError:
            return index

        for entry in entries:
            if not S_ISDIR(entry.st_mode):
                index[posixpath.join(remote_dir, entry.filename)] = {
                    "size": int(entry.st_size),
                    "mtime": int(entry.st_mtime),
                }
        self._log(f"Indexerade {len(index)} befintliga fjärrfiler i {remote_dir}")
        return index

    def _unique_remote_path(self, remote_file: str, remote_cache: dict[str, dict[str, object]]) -> str:
        directory = posixpath.dirname(remote_file)
        filename = posixpath.basename(remote_file)
        stem, extension = posixpath.splitext(filename)
        counter = 1
        while True:
            candidate = posixpath.join(directory, f"{stem} ({counter}){extension}")
            if candidate not in remote_cache:
                remote_cache[candidate] = {"size": -1, "mtime": 0}
                return candidate
            counter += 1

    def _reconnect(self, status: callable | None = None) -> None:
        last_error: Exception | None = None
        for attempt in range(1, TRANSFER_RETRY_ATTEMPTS + 1):
            try:
                self._log(f"Återansluter SSH/SFTP, försök {attempt}/{TRANSFER_RETRY_ATTEMPTS}")
                if status:
                    status(f"Återansluter till Raspberry Pi ({attempt}/{TRANSFER_RETRY_ATTEMPTS})...")
                self._connect_current()
                self._log("Återanslutning lyckades")
                return
            except Exception as exc:
                last_error = exc
                self._log_exception(f"Återanslutning misslyckades, försök {attempt}", exc)
                time.sleep(TRANSFER_RETRY_DELAY_SECONDS)

        raise RuntimeError(f"Kunde inte återansluta: {self._error_text(last_error)}")

    @staticmethod
    def _is_connection_error(exc: Exception) -> bool:
        text = f"{type(exc).__name__}: {exc}".lower()
        connection_markers = (
            "10054",
            "connection reset",
            "connection aborted",
            "connection closed",
            "socket",
            "eoferror",
            "server connection dropped",
            "no existing session",
            "not open",
        )
        ssh_exception = paramiko is not None and isinstance(exc, paramiko.SSHException)
        return isinstance(exc, (EOFError, OSError, socket.error)) or ssh_exception or any(
            marker in text for marker in connection_markers
        )

    def _mkdir_p(self, remote_path: str) -> None:
        self._require_sftp()
        normalized = posixpath.normpath(remote_path)
        if normalized in ("", "."):
            return

        parts = [part for part in normalized.split("/") if part]
        current = "/" if normalized.startswith("/") else "."
        for part in parts:
            current = posixpath.join(current, part) if current != "/" else f"/{part}"
            try:
                self.sftp.stat(current)
            except OSError:
                self._log(f"Skapar fjärrmapp: {current}")
                try:
                    self.sftp.mkdir(current)
                except Exception as exc:
                    self._log_exception(f"Kunde inte skapa fjärrmapp: {current}", exc)
                    raise RuntimeError(f"Kunde inte skapa fjärrmappen {current}: {self._error_text(exc)}") from exc

    @staticmethod
    def _count_files(path: Path) -> int:
        if path.is_file():
            return 1
        count = 0
        for root, _, files in os.walk(path):
            count += len(files)
        return count

    def _require_sftp(self) -> None:
        if self.sftp is None:
            raise RuntimeError("Inte ansluten till Raspberry Pi.")

    @staticmethod
    def _log(message: str) -> None:
        write_log(TRANSFER_LOG_PATH, message)

    @staticmethod
    def _log_exception(context: str, exc: Exception) -> None:
        write_log(TRANSFER_LOG_PATH, f"{context}: {SftpTransferClient._error_text(exc)}")
        write_log(TRANSFER_LOG_PATH, traceback.format_exc().rstrip())

    @staticmethod
    def _error_text(exc: Exception | None) -> str:
        if exc is None:
            return "okänt fel"
        text = str(exc).strip()
        return text if text else type(exc).__name__


class FileTransferApp:
    def __init__(self, root: Tk) -> None:
        self.root = root
        self.root.title(APP_TITLE)
        self.root.geometry("980x680")
        self.root.minsize(860, 600)

        self.queue: queue.Queue[tuple[str, object]] = queue.Queue()
        self.hosts: list[HostCandidate] = []
        self.local_paths: list[Path] = []
        self.remote_entries: list[RemoteEntry] = []
        self.sftp_client = SftpTransferClient()

        self.host_var = StringVar()
        self.username_var = StringVar(value="pi")
        self.password_var = StringVar()
        self.remote_path_var = StringVar(value="/home/pi")
        self.status_var = StringVar(value="Redo.")
        self.progress_var = StringVar(value="")
        self.auto_connecting = False

        self._build_ui()
        self.load_cached_hosts()
        self.root.after(350, self.discover_hosts)
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self.root.after(100, self._drain_queue)

    def _build_ui(self) -> None:
        outer = Frame(self.root, padx=14, pady=14)
        outer.pack(fill=BOTH, expand=True)
        outer.columnconfigure(0, weight=1)
        outer.columnconfigure(1, weight=1)
        outer.rowconfigure(1, weight=1)

        self._build_connection_panel(outer)
        self._build_local_panel(outer)
        self._build_remote_panel(outer)
        self._build_bottom_bar(outer)

    def _build_connection_panel(self, parent: Frame) -> None:
        panel = LabelFrame(parent, text="Raspberry Pi", padx=10, pady=10)
        panel.grid(row=0, column=0, columnspan=2, sticky=EW, pady=(0, 10))
        panel.columnconfigure(1, weight=1)
        panel.columnconfigure(3, weight=1)

        Label(panel, text="Hittade enheter").grid(row=0, column=0, sticky=W)
        self.host_combo = ttk.Combobox(panel, textvariable=self.host_var, state="normal")
        self.host_combo.grid(row=0, column=1, columnspan=3, sticky=EW, padx=(8, 0))
        self.host_combo.bind("<<ComboboxSelected>>", self._host_selected)

        Label(panel, text="Användare").grid(row=1, column=0, sticky=W, pady=(8, 0))
        Entry(panel, textvariable=self.username_var).grid(row=1, column=1, sticky=EW, padx=(8, 8), pady=(8, 0))

        Label(panel, text="Lösenord").grid(row=1, column=2, sticky=W, pady=(8, 0))
        self.password_entry = Entry(panel, textvariable=self.password_var, show="*")
        self.password_entry.grid(row=1, column=3, sticky=EW, padx=(8, 8), pady=(8, 0))
        self.password_entry.bind("<Return>", lambda _event: self.connect())

        self.connect_button = Button(panel, text="Anslut", command=self.connect)
        self.connect_button.grid(row=1, column=4, sticky=EW, pady=(8, 0))

    def _build_local_panel(self, parent: Frame) -> None:
        panel = LabelFrame(parent, text="Filer från den här datorn", padx=10, pady=10)
        panel.grid(row=1, column=0, sticky="nsew", padx=(0, 6))
        panel.columnconfigure(0, weight=1)
        panel.rowconfigure(0, weight=1)

        list_frame = Frame(panel)
        list_frame.grid(row=0, column=0, columnspan=4, sticky="nsew")
        list_frame.columnconfigure(0, weight=1)
        list_frame.rowconfigure(0, weight=1)

        self.local_list = Listbox(list_frame, selectmode="extended")
        self.local_list.grid(row=0, column=0, sticky="nsew")
        local_scroll = ttk.Scrollbar(list_frame, orient=VERTICAL, command=self.local_list.yview)
        local_scroll.grid(row=0, column=1, sticky="ns")
        self.local_list.configure(yscrollcommand=local_scroll.set)

        Button(panel, text="Lägg till filer", command=self.add_files).grid(row=1, column=0, sticky=EW, pady=(8, 0), padx=(0, 4))
        Button(panel, text="Lägg till mapp", command=self.add_folder).grid(row=1, column=1, sticky=EW, pady=(8, 0), padx=4)
        Button(panel, text="Ta bort", command=self.remove_selected_local).grid(row=1, column=2, sticky=EW, pady=(8, 0), padx=4)
        Button(panel, text="Rensa", command=self.clear_local).grid(row=1, column=3, sticky=EW, pady=(8, 0), padx=(4, 0))

    def _build_remote_panel(self, parent: Frame) -> None:
        panel = LabelFrame(parent, text="Destination på Raspberry Pi", padx=10, pady=10)
        panel.grid(row=1, column=1, sticky="nsew", padx=(6, 0))
        panel.columnconfigure(1, weight=1)
        panel.rowconfigure(2, weight=1)

        Label(panel, text="Sökväg").grid(row=0, column=0, sticky=W)
        self.remote_path_entry = Entry(panel, textvariable=self.remote_path_var)
        self.remote_path_entry.grid(row=0, column=1, columnspan=3, sticky=EW, padx=(8, 0))
        self.remote_path_entry.bind("<Return>", lambda _event: self.refresh_remote_dirs())

        Button(panel, text="Hem", command=self.remote_home).grid(row=1, column=0, sticky=EW, pady=(8, 0), padx=(0, 4))
        Button(panel, text="Upp", command=self.remote_up).grid(row=1, column=1, sticky=EW, pady=(8, 0), padx=4)
        Button(panel, text="Öppna sökväg", command=self.refresh_remote_dirs).grid(row=1, column=2, sticky=EW, pady=(8, 0), padx=4)
        Button(panel, text="Ny mapp", command=self.create_remote_folder).grid(row=1, column=3, sticky=EW, pady=(8, 0), padx=(4, 0))

        list_frame = Frame(panel)
        list_frame.grid(row=2, column=0, columnspan=4, sticky="nsew", pady=(8, 0))
        list_frame.columnconfigure(0, weight=1)
        list_frame.rowconfigure(0, weight=1)

        self.remote_list = Listbox(list_frame)
        self.remote_list.grid(row=0, column=0, sticky="nsew")
        self.remote_list.bind("<Double-Button-1>", self.enter_remote_dir)
        remote_scroll = ttk.Scrollbar(list_frame, orient=VERTICAL, command=self.remote_list.yview)
        remote_scroll.grid(row=0, column=1, sticky="ns")
        self.remote_list.configure(yscrollcommand=remote_scroll.set)

        self.delete_duplicates_button = Button(
            panel,
            text="Ta bort dubbletter",
            command=self.find_remote_duplicates,
        )
        self.delete_duplicates_button.grid(row=3, column=0, columnspan=4, sticky=EW, pady=(8, 0))

    def _build_bottom_bar(self, parent: Frame) -> None:
        bottom = Frame(parent)
        bottom.grid(row=2, column=0, columnspan=2, sticky=EW, pady=(10, 0))
        bottom.columnconfigure(0, weight=1)

        Label(bottom, textvariable=self.status_var, anchor=W).grid(row=0, column=0, sticky=EW)
        self.progress = ttk.Progressbar(bottom, mode="determinate", maximum=100)
        self.progress.grid(row=1, column=0, sticky=EW, pady=(6, 0), padx=(0, 10))
        Label(bottom, textvariable=self.progress_var, anchor=W, width=22).grid(row=1, column=1, sticky=EW)
        self.send_button = Button(bottom, text="Skicka", command=self.transfer, width=16)
        self.send_button.grid(row=0, column=1, sticky="e", rowspan=1)

    def load_cached_hosts(self) -> None:
        hosts = NetworkDiscovery.cached_hosts()
        if not hosts:
            return
        self._set_hosts(hosts)
        self.status_var.set(f"Laddade {len(hosts)} sparade enhet(er). Uppdaterar i bakgrunden.")

    def discover_hosts(self) -> None:
        self.status_var.set("Kontrollerar sparade enheter och söker vid behov...")

        def worker() -> None:
            try:
                hosts = NetworkDiscovery.discover(lambda message: self.queue.put(("status", message)))
                self.queue.put(("hosts", hosts))
            except Exception as exc:
                self.queue.put(("error", f"Upptäckt misslyckades: {exc}"))
            finally:
                self.queue.put(("discover_done", None))

        threading.Thread(target=worker, daemon=True).start()

    def _set_hosts(self, hosts: list[HostCandidate]) -> None:
        self.hosts = list(hosts)
        labels = [host.label for host in self.hosts]
        self.host_combo.configure(values=labels)
        if labels:
            current = self.host_var.get().strip()
            if current not in labels:
                self.host_var.set(labels[0])
            self._apply_saved_credentials_for_selected_host()
            self._auto_connect_if_possible()

    def connect(self) -> None:
        host = self._selected_host_address()
        username = self.username_var.get().strip()
        password = self.password_var.get()

        if not host:
            messagebox.showwarning(APP_TITLE, "Välj en hittad enhet eller skriv IP-adress/hostnamn.")
            return
        if not username:
            messagebox.showwarning(APP_TITLE, "Ange användarnamn för Raspberry Pi.")
            return

        self.connect_button.configure(state=DISABLED)
        self.status_var.set(f"Ansluter till {host}...")

        def worker() -> None:
            try:
                home = self.sftp_client.connect(host, username, password or None, None)
                self._save_credentials_for_host(host, username, password)
                self.queue.put(("connected", home))
            except Exception as exc:
                self.queue.put(("error", f"Kunde inte ansluta: {exc}"))
            finally:
                self.queue.put(("connect_done", None))

        threading.Thread(target=worker, daemon=True).start()

    def add_files(self) -> None:
        filenames = filedialog.askopenfilenames(title="Välj filer")
        for filename in filenames:
            self._add_local_path(Path(filename))

    def add_folder(self) -> None:
        folder = filedialog.askdirectory(title="Välj mapp")
        if folder:
            self._add_local_path(Path(folder))

    def remove_selected_local(self) -> None:
        selected = list(self.local_list.curselection())
        for index in reversed(selected):
            self.local_list.delete(index)
            del self.local_paths[index]

    def clear_local(self) -> None:
        self.local_list.delete(0, END)
        self.local_paths.clear()

    def refresh_remote_dirs(self) -> None:
        path = self.remote_path_var.get().strip() or "."
        self.status_var.set(f"Läser {path}...")

        def worker() -> None:
            try:
                normalized = self.sftp_client.normalize(path)
                entries = self.sftp_client.list_entries(normalized)
                self.queue.put(("remote_entries", (normalized, entries)))
            except Exception as exc:
                self.queue.put(("error", f"Kunde inte läsa destinationen: {exc}"))

        threading.Thread(target=worker, daemon=True).start()

    def remote_home(self) -> None:
        def worker() -> None:
            try:
                home = self.sftp_client.normalize(".")
                entries = self.sftp_client.list_entries(home)
                self.queue.put(("remote_entries", (home, entries)))
            except Exception as exc:
                self.queue.put(("error", f"Kunde inte öppna hemkatalogen: {exc}"))

        threading.Thread(target=worker, daemon=True).start()

    def remote_up(self) -> None:
        current = self.remote_path_var.get().strip() or "."
        parent = posixpath.dirname(current.rstrip("/")) or "/"
        self.remote_path_var.set(parent)
        self.refresh_remote_dirs()

    def enter_remote_dir(self, _event=None) -> None:
        selected = self.remote_list.curselection()
        if not selected:
            return
        entry = self.remote_entries[selected[0]]
        if not entry.is_dir:
            self.status_var.set(f"Vald fil: {entry.name}")
            return
        next_path = posixpath.join(self.remote_path_var.get().strip() or ".", entry.name)
        self.remote_path_var.set(next_path)
        self.refresh_remote_dirs()

    def create_remote_folder(self) -> None:
        name = self._ask_text("Ny mapp", "Mappnamn:")
        if not name:
            return
        if "/" in name or "\\" in name:
            messagebox.showwarning(APP_TITLE, "Ange bara ett mappnamn, inte en hel sökväg.")
            return

        target = posixpath.join(self.remote_path_var.get().strip() or ".", name)

        def worker() -> None:
            try:
                self.sftp_client.mkdir(target)
                self.queue.put(("status", f"Skapade {target}."))
                self.queue.put(("refresh", None))
            except Exception as exc:
                self.queue.put(("error", f"Kunde inte skapa mapp: {exc}"))

        threading.Thread(target=worker, daemon=True).start()

    def find_remote_duplicates(self) -> None:
        path = self.remote_path_var.get().strip() or "."
        self.delete_duplicates_button.configure(state=DISABLED)
        self.status_var.set(f"Söker dubbletter under {path}...")

        def worker() -> None:
            try:
                normalized = self.sftp_client.normalize(path)
                duplicates = self.sftp_client.find_duplicate_files(normalized)
                self.queue.put(("duplicate_candidates", (normalized, duplicates)))
            except Exception as exc:
                self.queue.put(("error", f"Kunde inte söka dubbletter: {exc}"))
                self.queue.put(("duplicates_done", None))

        threading.Thread(target=worker, daemon=True).start()

    def delete_remote_duplicates(self, path: str, duplicates: list[str]) -> None:
        self.delete_duplicates_button.configure(state=DISABLED)
        self.status_var.set(f"Tar bort {len(duplicates)} dubblettfil(er)...")

        def worker() -> None:
            try:
                deleted, failed = self.sftp_client.delete_files(duplicates)
                self.queue.put(("duplicates_deleted", (path, deleted, failed)))
            except Exception as exc:
                self.queue.put(("error", f"Kunde inte ta bort dubbletter: {exc}"))
            finally:
                self.queue.put(("duplicates_done", None))

        threading.Thread(target=worker, daemon=True).start()

    def transfer(self) -> None:
        if not self.local_paths:
            messagebox.showwarning(APP_TITLE, "Lägg till minst en fil eller mapp först.")
            return

        destination = self.remote_path_var.get().strip()
        if not destination:
            messagebox.showwarning(APP_TITLE, "Ange destination på Raspberry Pi.")
            return

        self.send_button.configure(state=DISABLED)
        self.progress.configure(value=0)
        self.progress_var.set("")
        self.status_var.set("Planerar överföring och kontrollerar befintliga filer...")

        paths = list(self.local_paths)

        def progress(done: int, total: int, filename: str, action: str) -> None:
            self.queue.put(("transfer_progress", (done, total, filename, action)))

        def transfer_status(message: str) -> None:
            self.queue.put(("status", message))

        def worker() -> None:
            try:
                self.sftp_client.upload_paths(paths, destination, progress, transfer_status)
                self.queue.put(("transfer_done", None))
            except Exception as exc:
                detail = self._exception_text(exc)
                write_log(TRANSFER_LOG_PATH, f"Överföring misslyckades i GUI-worker: {detail}")
                write_log(TRANSFER_LOG_PATH, traceback.format_exc().rstrip())
                self.queue.put(("error", f"Överföring misslyckades: {detail}"))
            finally:
                self.queue.put(("send_done", None))

        threading.Thread(target=worker, daemon=True).start()

    def _add_local_path(self, path: Path) -> None:
        path = path.resolve()
        if path in self.local_paths:
            return
        self.local_paths.append(path)
        self.local_list.insert(END, str(path))

    def _selected_host_address(self) -> str:
        value = self.host_var.get().strip()
        for host in self.hosts:
            if value == host.label:
                return host.address
        match = re.search(r"\(([^)]+)\)", value)
        return match.group(1).strip() if match else value

    def _host_selected(self, _event=None) -> None:
        self._apply_saved_credentials_for_selected_host()
        self.status_var.set(f"Vald enhet: {self._selected_host_address()}")
        self._auto_connect_if_possible()

    def _selected_host_candidate(self) -> HostCandidate | None:
        address = self._selected_host_address()
        for host in self.hosts:
            if host.address == address:
                return host
        return None

    def _apply_saved_credentials_for_selected_host(self) -> None:
        host = self._selected_host_candidate()
        if not host:
            return

        self.username_var.set(host.username or "pi")
        self.password_var.set("")

        password = CredentialStore.password_for(host.address)
        if password:
            self.password_var.set(password)

    def _auto_connect_if_possible(self) -> None:
        if self.auto_connecting or self.sftp_client.sftp is not None:
            return
        host = self._selected_host_candidate()
        if not host or not host.username:
            return
        if not CredentialStore.password_for(host.address):
            return
        self.auto_connecting = True
        self.root.after(100, self.connect)

    def _save_credentials_for_host(
        self,
        address: str,
        username: str,
        password: str,
    ) -> None:
        if password:
            CredentialStore.save_password(address, password)

        updated_hosts: list[HostCandidate] = []
        matched = False
        for host in self.hosts:
            if host.address == address:
                updated_hosts.append(HostCandidate(host.name, host.address, host.source, username, False, ""))
                matched = True
            else:
                updated_hosts.append(host)

        if not matched:
            updated_hosts.append(HostCandidate(address, address, "manuell", username, False, ""))

        NetworkDiscovery.save_cache(updated_hosts)
        self.queue.put(("hosts", NetworkDiscovery.cached_hosts()))

    @staticmethod
    def _exception_text(exc: Exception) -> str:
        text = str(exc).strip()
        return text if text else type(exc).__name__

    def _ask_text(self, title: str, prompt: str) -> str | None:
        dialog = TkTextDialog(self.root, title, prompt)
        self.root.wait_window(dialog.window)
        return dialog.value

    def _drain_queue(self) -> None:
        while True:
            try:
                message, payload = self.queue.get_nowait()
            except queue.Empty:
                break

            if message == "status":
                self.status_var.set(str(payload))
            elif message == "hosts":
                hosts = list(payload)
                self._set_hosts(hosts)
                if hosts:
                    self.status_var.set(f"Hittade {len(hosts)} SSH-enhet(er).")
                else:
                    self.status_var.set("Hittade ingen Pi automatiskt. Skriv IP-adressen manuellt.")
            elif message == "discover_done":
                pass
            elif message == "connected":
                self.remote_path_var.set(str(payload))
                self.status_var.set("Ansluten.")
                self.refresh_remote_dirs()
            elif message == "connect_done":
                self.auto_connecting = False
                self.connect_button.configure(state=NORMAL)
            elif message == "remote_entries":
                path, entries = payload
                self.remote_entries = list(entries)
                self.remote_path_var.set(path)
                self.remote_list.delete(0, END)
                for entry in self.remote_entries:
                    self.remote_list.insert(END, entry.display_name)
                dir_count = sum(1 for entry in self.remote_entries if entry.is_dir)
                file_count = len(self.remote_entries) - dir_count
                self.status_var.set(f"Visar {path}. {dir_count} mappar, {file_count} filer.")
            elif message == "refresh":
                self.refresh_remote_dirs()
            elif message == "duplicate_candidates":
                path, duplicates = payload
                duplicates = list(duplicates)
                if not duplicates:
                    self.status_var.set(f"Inga dubbletter hittades under {path}.")
                    self.delete_duplicates_button.configure(state=NORMAL)
                    messagebox.showinfo(APP_TITLE, "Inga dubbletter hittades.")
                    continue

                root = str(path).rstrip("/")
                examples = []
                for duplicate in duplicates[:8]:
                    duplicate = str(duplicate)
                    prefix = f"{root}/"
                    examples.append(duplicate[len(prefix):] if duplicate.startswith(prefix) else duplicate)
                more = "" if len(duplicates) <= len(examples) else f"\n...och {len(duplicates) - len(examples)} till."
                confirmed = messagebox.askyesno(
                    APP_TITLE,
                    "Ta bort dessa dubblettfiler från Raspberry Pi?\n\n"
                    f"Antal: {len(duplicates)}\n\n"
                    + "\n".join(examples)
                    + more,
                )
                if confirmed:
                    self.delete_remote_duplicates(str(path), duplicates)
                else:
                    self.status_var.set("Borttagning av dubbletter avbröts.")
                    self.delete_duplicates_button.configure(state=NORMAL)
            elif message == "duplicates_deleted":
                path, deleted, failed = payload
                deleted = list(deleted)
                failed = list(failed)
                self.refresh_remote_dirs()
                if failed:
                    self.status_var.set(f"Tog bort {len(deleted)} dubblettfil(er). {len(failed)} misslyckades.")
                    messagebox.showwarning(
                        APP_TITLE,
                        f"Tog bort {len(deleted)} dubblettfil(er), men {len(failed)} kunde inte tas bort. "
                        "Se transfer.log för detaljer.",
                    )
                else:
                    self.status_var.set(f"Tog bort {len(deleted)} dubblettfil(er) under {path}.")
                    messagebox.showinfo(APP_TITLE, f"Tog bort {len(deleted)} dubblettfil(er).")
            elif message == "duplicates_done":
                self.delete_duplicates_button.configure(state=NORMAL)
            elif message == "transfer_progress":
                done, total, filename, action = payload
                percent = int((done / total) * 100) if total else 100
                self.progress.configure(value=percent)
                self.progress_var.set(f"{done}/{total}")
                if action == "skipped":
                    self.status_var.set(f"Hoppar över redan överförd fil: {filename}")
                elif action == "renamed":
                    self.status_var.set(f"Skickar utan att skriva över: {filename}")
                else:
                    self.status_var.set(f"Skickar: {filename}")
            elif message == "transfer_done":
                self.progress.configure(value=100)
                self.progress_var.set("Klart")
                self.status_var.set("Överföringen är klar.")
                messagebox.showinfo(APP_TITLE, "Filerna har skickats till Raspberry Pi.")
            elif message == "send_done":
                self.send_button.configure(state=NORMAL)
            elif message == "error":
                error_text = str(payload).strip() or "Ett okänt fel inträffade. Se transfer.log."
                self.status_var.set(error_text)
                messagebox.showerror(APP_TITLE, error_text)

        self.root.after(100, self._drain_queue)

    def _on_close(self) -> None:
        self.sftp_client.close()
        self.root.destroy()


class TkTextDialog:
    def __init__(self, parent: Tk, title: str, prompt: str) -> None:
        self.value: str | None = None
        self.window = ttk.Toplevel(parent) if hasattr(ttk, "Toplevel") else None
        if self.window is None:
            from tkinter import Toplevel

            self.window = Toplevel(parent)

        self.window.title(title)
        self.window.transient(parent)
        self.window.grab_set()
        self.window.resizable(False, False)

        frame = Frame(self.window, padx=12, pady=12)
        frame.pack(fill=BOTH, expand=True)
        Label(frame, text=prompt).pack(anchor=W)
        self.entry = Entry(frame, width=34)
        self.entry.pack(fill=X, pady=(8, 12))
        self.entry.focus_set()

        buttons = Frame(frame)
        buttons.pack(fill=X)
        Button(buttons, text="Avbryt", command=self._cancel).pack(side=RIGHT, padx=(6, 0))
        Button(buttons, text="OK", command=self._ok).pack(side=RIGHT)

        self.entry.bind("<Return>", lambda _event: self._ok())
        self.entry.bind("<Escape>", lambda _event: self._cancel())

        parent.update_idletasks()
        x = parent.winfo_rootx() + (parent.winfo_width() // 2) - 160
        y = parent.winfo_rooty() + (parent.winfo_height() // 2) - 70
        self.window.geometry(f"+{x}+{y}")

    def _ok(self) -> None:
        self.value = self.entry.get().strip()
        self.window.destroy()

    def _cancel(self) -> None:
        self.value = None
        self.window.destroy()


def main() -> None:
    root = Tk()
    app = FileTransferApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
