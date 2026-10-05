"""Private loopback sshd fixture. Does not read/write the user's SSH configuration."""
import getpass
import json
import os
from pathlib import Path
import select
import shlex
import shutil
import signal
import socket
import subprocess
import sys
import uuid


def setup(destination):
    local = Path(destination).resolve()
    local.mkdir(parents=True, exist_ok=True)
    parent = Path.home() / '.cache' / 'sourcegit-ssh-fixtures'
    parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    root = parent / uuid.uuid4().hex
    root.mkdir(mode=0o700)
    home = root / 'home'
    home.mkdir(mode=0o700)
    for name in ('host', 'client'):
        subprocess.run(['ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-f', str(root / name)], check=True)
    shutil.copyfile(root / 'client.pub', root / 'authorized_keys')
    os.chmod(root / 'authorized_keys', 0o600)
    shutil.copyfile(root / 'client', local / 'client')
    with socket.socket() as candidate:
        candidate.bind(('127.0.0.1', 0))
        port = candidate.getsockname()[1]
    user = getpass.getuser()
    wrapper = root / 'command'
    wrapper.write_text('#!/bin/sh\nexport HOME=' + shlex.quote(str(home)) + '\ncase "$SSH_ORIGINAL_COMMAND" in\n*--wait-only*) ;;\n*" bootstrap --record "*) printf "start\\n" >> "$HOME/start-calls" ;;\nesac\nexec /bin/sh -c "$SSH_ORIGINAL_COMMAND"\n')
    wrapper.chmod(0o700)
    config = root / 'sshd_config'
    config.write_text(f'''Port {port}
ListenAddress 127.0.0.1
AddressFamily inet
HostKey {root}/host
PidFile {root}/sshd.pid
AuthorizedKeysFile {root}/authorized_keys
AllowUsers {user}
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
StrictModes yes
AllowTcpForwarding local
AllowAgentForwarding no
X11Forwarding no
ForceCommand {wrapper}
LogLevel VERBOSE
''')
    # OpenSSH's compiled privilege-separation directory is shared infrastructure;
    # create it only if absent, do not change existing permissions or remove it.
    subprocess.run(['sudo', '-n', 'mkdir', '-p', '/run/sshd'], check=True)
    subprocess.run(['sudo', '-n', '/usr/sbin/sshd', '-t', '-f', str(config)], check=True)
    host = (root / 'host.pub').read_text().split()
    (local / 'known_hosts').write_text(f'[127.0.0.1]:{port} {host[0]} {host[1]}\n')
    windows = subprocess.check_output(['wslpath', '-w', str(local)], text=True).strip().replace('\\', '/')
    (local / 'ssh_config').write_text(f'''Host sourcegit-fixture
    HostName 127.0.0.1
    Port {port}
    User {user}
    IdentityFile "{windows}/client"
    UserKnownHostsFile "{windows}/known_hosts"
    GlobalKnownHostsFile none
    IdentitiesOnly yes
    StrictHostKeyChecking yes
    BatchMode yes
''')
    metadata = {'root': str(root), 'home': str(home), 'port': port, 'user': user}
    (local / 'fixture.json').write_text(json.dumps(metadata))
    print(json.dumps(metadata))


def refresh(metadata):
    root = Path(json.loads(Path(metadata).read_text())['root'])
    home = root / 'home'
    (root / 'command').write_text('#!/bin/sh\nexport HOME=' + shlex.quote(str(home)) + '\ncase "$SSH_ORIGINAL_COMMAND" in\n*--wait-only*) ;;\n*" bootstrap --record "*) printf "start\\n" >> "$HOME/start-calls" ;;\nesac\nexec /bin/sh -c "$SSH_ORIGINAL_COMMAND"\n')
    print('Fixture startup counter enabled')


def stop(metadata):
    root = Path(json.loads(Path(metadata).read_text())['root'])
    if root.parent.name != 'sourcegit-ssh-fixtures' or root.parent.parent.name != '.cache' or len(root.name) != 32 or any(character not in '0123456789abcdef' for character in root.name):
        raise RuntimeError('Untrusted fixture root')
    # Only signal the daemon identified by this private config and its stable pidfd.
    pidfile = root / 'sshd.pid'
    if pidfile.exists():
        pid = int(pidfile.read_text())
        try:
            fd = os.pidfd_open(pid)
        except ProcessLookupError:
            fd = None
        if fd is not None:
            try:
                arguments = Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')
                if str(root / 'sshd_config').encode() not in b' '.join(arguments) or b'/usr/sbin/sshd' not in b' '.join(arguments):
                    raise RuntimeError('Daemon identity changed; preserved')
                signal.pidfd_send_signal(fd, signal.SIGTERM)
                poll = select.poll()
                poll.register(fd, select.POLLIN)
                if not poll.poll(15000):
                    raise TimeoutError('Fixture daemon did not exit')
            finally:
                os.close(fd)
    # Keep package/log evidence, remove only fixture credentials.
    for name in ('client', 'client.pub', 'authorized_keys', 'host', 'host.pub'):
        (root / name).unlink(missing_ok=True)
    print('PASS Isolated SSH daemon stopped; private fixture credentials removed')


if sys.argv[1] == 'setup':
    setup(sys.argv[2])
elif sys.argv[1] == 'stop':
    stop(sys.argv[2])
elif sys.argv[1] == 'refresh':
    refresh(sys.argv[2])
else:
    raise ValueError('Unknown fixture action')
