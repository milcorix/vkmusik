#!/usr/bin/env bash
# Локальная установка VK Музыки в домашний каталог (без root).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${PREFIX:-$HOME/.local}"
APPDIR="$PREFIX/share/vkmusik"
BINDIR="$PREFIX/bin"

die() { printf '\033[31mОшибка:\033[0m %s\n' "$1" >&2; exit 1; }
info() { printf '\033[34m==>\033[0m %s\n' "$1"; }

command -v dotnet >/dev/null || die "не найден dotnet. Установите: sudo pacman -S dotnet-sdk"
command -v ffmpeg >/dev/null || die "не найден ffmpeg. Установите: sudo pacman -S ffmpeg"

if ! ls /usr/lib/libpulse-simple.so* >/dev/null 2>&1; then
  printf '\033[33mВнимание:\033[0m libpulse-simple не найдена — звук пойдёт через pw-cat/aplay.\n'
  printf '            Для лучшего результата: sudo pacman -S libpulse\n'
fi

info "Сборка (Release)…"
dotnet publish "$ROOT/src/VkMusik/VkMusik.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -o "$APPDIR" \
  --nologo -v minimal

info "Установка ярлыка запуска…"
mkdir -p "$BINDIR"
cat > "$BINDIR/vkmusik" <<EOF
#!/usr/bin/env bash
exec "$APPDIR/vkmusik" "\$@"
EOF
chmod +x "$BINDIR/vkmusik"

info "Установка значка и пункта меню…"
install -Dm644 "$ROOT/src/VkMusik/Assets/vkmusik.svg" \
  "$PREFIX/share/icons/hicolor/scalable/apps/vkmusik.svg"
install -Dm644 "$ROOT/src/VkMusik/Assets/icon.png" \
  "$PREFIX/share/icons/hicolor/256x256/apps/vkmusik.png"
install -Dm644 "$ROOT/packaging/vkmusik.desktop" \
  "$PREFIX/share/applications/vkmusik.desktop"

command -v update-desktop-database >/dev/null && \
  update-desktop-database "$PREFIX/share/applications" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null && \
  gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" 2>/dev/null || true

echo
info "Готово. Запуск: vkmusik (или «VK Музыка» в меню приложений)"
case ":$PATH:" in
  *":$BINDIR:"*) ;;
  *) printf '\033[33mДобавьте в PATH:\033[0m export PATH="%s:$PATH"\n' "$BINDIR" ;;
esac
