# Transcribed directly from libraries/murmur2/src/MurmurHash2.cpp, independently of the C# port.
M = 0x5bd1e995
R = 24
MASK = 0xFFFFFFFF

def four_bytes(data, h, ln):
    if ln >= 4:
        k = int.from_bytes(bytes(data[:4]), 'little')
        k = (k * M) & MASK
        k ^= k >> R
        k = (k * M) & MASK
        h = (h * M) & MASK
        h ^= k
        ln -= 4
    else:
        if ln == 3:
            h ^= data[2] << 16
        if ln >= 2:
            h ^= data[1] << 8
        if ln >= 1:
            h ^= data[0]
            h = (h * M) & MASK
        h &= MASK
        h ^= h >> 13
        h = (h * M) & MASK
        h ^= h >> 15
        ln = 0
    return h & MASK, ln

def murmur2(buf):
    filt = lambda c: c in (9, 10, 13, 32)
    size = sum(1 for c in buf if not filt(c))
    h, ln = (1 ^ size) & MASK, size
    data = [0, 0, 0, 0]
    idx = 0
    for c in buf:
        if filt(c):
            continue
        data[idx] = c
        idx = (idx + 1) % 4
        if idx == 0:
            h, ln = four_bytes(data, h, ln)
    h, ln = four_bytes(data, h, ln)
    return h

if __name__ == "__main__":
    import random
    random.seed(20260818)
    cases = [b"", b"a", b"ab", b"abc", b"abcd", b"abcde",
             b"  \t\r\n  ", b"a b\tc\r\nd",
             b"The quick brown fox jumps over the lazy dog",
             bytes(range(256))]
    for n in (1, 2, 3, 4, 5, 7, 8, 15, 16, 17, 63, 64, 100, 1000, 5000):
        cases.append(bytes(random.randrange(256) for _ in range(n)))
    for c in cases:
        print(c.hex(), murmur2(c))
