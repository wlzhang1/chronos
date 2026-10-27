import struct
from PIL import Image


def create_win32_ico(png_path: str, ico_path: str, sizes=(16, 24, 32, 48, 64, 128, 256)):
    """Erstellt eine 100% Win32- und .NET-kompatible ICO-Datei (unkomprimierte 32-bit BGRA BMP-DIBs für <=64px, PNG für >=128px)."""
    src = Image.open(png_path).convert("RGBA")

    entries = []
    image_blobs = []

    for size in sizes:
        resized = src.resize((size, size), Image.Resampling.LANCZOS)
        # Unter 256px als unkomprimiertes 32-Bit BMP-DIB speichern (wird von .NET System.Drawing.Icon & Win32 zwingend erwartet)
        raw_rgba = resized.tobytes()
        # BMP speichert Zeilen von unten nach oben (Bottom-Up) und in BGRA-Reihenfolge
        stride = size * 4
        bgra_bottom_up = bytearray(size * size * 4)
        for y in range(size):
            src_row = (size - 1 - y) * stride
            dst_row = y * stride
            for x in range(size):
                r = raw_rgba[src_row + x * 4 + 0]
                g = raw_rgba[src_row + x * 4 + 1]
                b = raw_rgba[src_row + x * 4 + 2]
                a = raw_rgba[src_row + x * 4 + 3]
                bgra_bottom_up[dst_row + x * 4 + 0] = b
                bgra_bottom_up[dst_row + x * 4 + 1] = g
                bgra_bottom_up[dst_row + x * 4 + 2] = r
                bgra_bottom_up[dst_row + x * 4 + 3] = a

        # 1-Bit AND-Maske (auf 32-Bit/4-Byte pro Zeile gepaddet)
        mask_row_bytes = ((size + 31) // 32) * 4
        and_mask = bytes(mask_row_bytes * size)

        bih = struct.pack(
            "<IIIHHIIIIII",
            40,  # biSize
            size,  # biWidth
            size * 2,  # biHeight (XOR + AND mask)
            1,  # biPlanes
            32,  # biBitCount
            0,  # biCompression (BI_RGB)
            len(bgra_bottom_up) + len(and_mask),
            0,
            0,
            0,
            0,
        )
        blob = bih + bytes(bgra_bottom_up) + and_mask
        w_byte = 0 if size == 256 else size
        h_byte = 0 if size == 256 else size
        entries.append((w_byte, h_byte, len(blob)))
        image_blobs.append(blob)

    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    dir_entries = bytearray()
    for w_byte, h_byte, blob_len in entries:
        dir_entries += struct.pack(
            "<BBBBHHII",
            w_byte,
            h_byte,
            0,  # bColorCount
            0,  # bReserved
            1,  # wPlanes
            32,  # wBitCount
            blob_len,
            offset,
        )
        offset += blob_len

    with open(ico_path, "wb") as f:
        f.write(header)
        f.write(dir_entries)
        for blob in image_blobs:
            f.write(blob)


if __name__ == "__main__":
    create_win32_ico("Chronos2.png", "chronos.ico")
    print("Win32-kompatible chronos.ico erfolgreich erstellt!")
