/* SPDX-License-Identifier: GPL-3.0-only */
#include <fcntl.h>
#include <io.h>
#include <stdio.h>
#include <string.h>
#include "playfair.h"

int main(void)
{
    unsigned char message[164];
    unsigned char wrapped[72];
    unsigned char key[16];

    if (_setmode(_fileno(stdin), _O_BINARY) < 0 || _setmode(_fileno(stdout), _O_BINARY) < 0)
        return 1;
    if (fread(message, 1, sizeof message, stdin) != sizeof message ||
        fread(wrapped, 1, sizeof wrapped, stdin) != sizeof wrapped)
        return 2;
    if (memcmp(message, "FPLY\x03\x01\x03\x00\x00\x00\x00\x98", 12) != 0 ||
        message[12] > 3 || memcmp(wrapped, "FPLY\x01\x02\x01\x00\x00\x00\x00\x3c", 12) != 0)
        return 3;

    playfair_decrypt(message, wrapped, key);
    return fwrite(key, 1, sizeof key, stdout) == sizeof key && fflush(stdout) == 0 ? 0 : 4;
}
