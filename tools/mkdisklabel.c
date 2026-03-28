/*
 * mkdisklabel - Update a NetBSD/mvme68k VID disklabel in a raw disk image
 *
 * Two modes:
 *   "create" - Write a fresh VID block (for new images)
 *   "update" - Read existing VID block, update geometry and partition sizes
 *              (preserves filesystem offsets and format fields)
 *
 * Designed for MVME147 SCSI disk geometry (64 heads, 32 sectors, 512 bytes/sector).
 *
 * Usage:
 *   mkdisklabel create <image> <total_sectors> <swap_sectors>
 *   mkdisklabel update <image> <total_sectors> <swap_sectors>
 */

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>

#define DISKMAGIC       0x82564557
#define SECTOR_SIZE     512
#define NSECTORS        32
#define NTRACKS         64
#define SECPERCYL       (NSECTORS * NTRACKS)  /* 2048 */

static void put_be16(uint8_t *buf, int offset, uint16_t v)
{
	buf[offset]     = (v >> 8) & 0xFF;
	buf[offset + 1] = v & 0xFF;
}

static void put_be32(uint8_t *buf, int offset, uint32_t v)
{
	buf[offset]     = (v >> 24) & 0xFF;
	buf[offset + 1] = (v >> 16) & 0xFF;
	buf[offset + 2] = (v >> 8) & 0xFF;
	buf[offset + 3] = v & 0xFF;
}

static uint32_t get_be32(const uint8_t *buf, int offset)
{
	return ((uint32_t)buf[offset] << 24) |
	       ((uint32_t)buf[offset + 1] << 16) |
	       ((uint32_t)buf[offset + 2] << 8) |
	       (uint32_t)buf[offset + 3];
}

static void set_string(uint8_t *buf, int offset, int maxlen, const char *s)
{
	int len = (int)strlen(s);
	if (len > maxlen) len = maxlen;
	memcpy(buf + offset, s, len);
}

/*
 * Write a fresh VID block (for new empty images).
 */
static void create_label(uint8_t *sector, uint32_t total_sectors,
    uint32_t swap_sectors)
{
	uint32_t ncylinders = total_sectors / SECPERCYL;
	uint32_t a_sectors = total_sectors - swap_sectors;
	uint32_t b_offset = a_sectors;
	int pa = 0x98;

	memset(sector, 0, SECTOR_SIZE);

	/* VID area */
	set_string(sector, 0x00, 4, "NBSD");
	put_be32(sector, 0x14, 2);
	put_be16(sector, 0x18, 30);
	put_be16(sector, 0x1E, 0x003F);
	put_be16(sector, 0x24, 8);
	set_string(sector, 0x26, 16, "EMULATED");
	put_be32(sector, 0x36, 8192);
	put_be32(sector, 0x3A, DISKMAGIC);
	put_be16(sector, 0x3E, 4);
	set_string(sector, 0x42, 16, "NetBSD");
	put_be32(sector, 0x80, SECPERCYL);
	put_be32(sector, 0x84, total_sectors);
	put_be32(sector, 0x90, 1);
	sector[0x94] = 1;

	/* Partition a: root (starts at sector 0) */
	put_be32(sector, pa + 0, a_sectors);
	put_be32(sector, pa + 4, 0);
	put_be32(sector, pa + 8, 1024);
	sector[pa + 12] = 7;   /* FS_BSDFFS */
	sector[pa + 13] = 8;
	put_be16(sector, pa + 14, 16);

	/* Partition b: swap */
	put_be32(sector, pa + 16 + 0, swap_sectors);
	put_be32(sector, pa + 16 + 4, b_offset);
	sector[pa + 16 + 12] = 1;  /* FS_SWAP */

	/* Partition c: whole disk */
	put_be32(sector, pa + 32 + 0, total_sectors);
	put_be32(sector, pa + 32 + 4, 0);

	put_be32(sector, 0xF4, 8192);
	set_string(sector, 0xF8, 8, "MOTOROLA");

	/* CFG area */
	put_be16(sector, 0x10A, 256);
	put_be16(sector, 0x114, 3600);
	sector[0x118] = NSECTORS;
	sector[0x119] = NTRACKS;
	put_be16(sector, 0x11A, (uint16_t)ncylinders);
	sector[0x11C] = 1;
	put_be16(sector, 0x11E, 512);
	put_be32(sector, 0x13C, DISKMAGIC);
}

/*
 * Update an existing VID block: preserve partition offsets and format,
 * only update geometry and partition sizes for expansion.
 */
static void update_label(uint8_t *sector, uint32_t total_sectors,
    uint32_t swap_sectors)
{
	uint32_t ncylinders = total_sectors / SECPERCYL;
	int pa = 0x98;
	uint32_t a_offset, a_size, b_offset;

	/* Preserve existing partition a offset (filesystem location) */
	a_offset = get_be32(sector, pa + 4);
	a_size = total_sectors - swap_sectors - a_offset;
	b_offset = a_offset + a_size;

	/* Update geometry */
	put_be32(sector, 0x80, SECPERCYL);
	put_be32(sector, 0x84, total_sectors);

	/* Update partition a: expand size, keep offset and format */
	put_be32(sector, pa + 0, a_size);
	/* pa + 4 (offset) unchanged */
	/* pa + 8..15 (fsize, fstype, frag, cpg) unchanged */

	/* Update partition b: relocate to after expanded a */
	put_be32(sector, pa + 16 + 0, swap_sectors);
	put_be32(sector, pa + 16 + 4, b_offset);

	/* Update partition c: whole disk */
	put_be32(sector, pa + 32 + 0, total_sectors);

	/* Update CFG geometry */
	put_be16(sector, 0x11A, (uint16_t)ncylinders);

	printf("  Preserved sd0a offset: %u (sector)\n", a_offset);
}

int main(int argc, char *argv[])
{
	FILE *fp;
	uint8_t sector[SECTOR_SIZE];
	uint32_t total_sectors, swap_sectors;
	uint32_t ncylinders, a_offset, a_size, b_offset, b_size;
	int create_mode;
	int pa = 0x98;

	if (argc < 5) {
		fprintf(stderr,
		    "Usage: %s <create|update> <image> <total_sectors> <swap_sectors>\n",
		    argv[0]);
		return 1;
	}

	if (strcmp(argv[1], "create") == 0)
		create_mode = 1;
	else if (strcmp(argv[1], "update") == 0)
		create_mode = 0;
	else {
		fprintf(stderr, "Error: first argument must be 'create' or 'update'\n");
		return 1;
	}

	total_sectors = (uint32_t)strtoul(argv[3], NULL, 0);
	swap_sectors  = (uint32_t)strtoul(argv[4], NULL, 0);
	ncylinders = total_sectors / SECPERCYL;

	/* Read existing sector 0 (for update mode) */
	fp = fopen(argv[2], "r+b");
	if (!fp) {
		perror(argv[2]);
		return 1;
	}

	if (fread(sector, 1, SECTOR_SIZE, fp) != SECTOR_SIZE) {
		/* New/empty file: fill with zeros */
		memset(sector, 0, SECTOR_SIZE);
	}

	if (create_mode) {
		create_label(sector, total_sectors, swap_sectors);
	} else {
		/* Verify existing label */
		uint32_t magic = get_be32(sector, 0x3A);
		if (magic != DISKMAGIC) {
			fprintf(stderr, "Error: no valid VID disklabel found "
			    "(magic=0x%08x, expected 0x%08x)\n",
			    magic, DISKMAGIC);
			fclose(fp);
			return 1;
		}
		update_label(sector, total_sectors, swap_sectors);
	}

	/* Write sector 0 */
	fseek(fp, 0, SEEK_SET);
	if (fwrite(sector, 1, SECTOR_SIZE, fp) != SECTOR_SIZE) {
		perror("fwrite");
		fclose(fp);
		return 1;
	}
	fclose(fp);

	/* Print result */
	a_size = get_be32(sector, pa + 0);
	a_offset = get_be32(sector, pa + 4);
	b_size = get_be32(sector, pa + 16 + 0);
	b_offset = get_be32(sector, pa + 16 + 4);

	printf("VID disklabel %s: %s\n",
	    create_mode ? "created" : "updated", argv[2]);
	printf("  Geometry: %u cyl, %d head, %d sec, %d bytes/sec\n",
	    ncylinders, NTRACKS, NSECTORS, SECTOR_SIZE);
	printf("  sd0a: offset=%u size=%u (%u MB) type=4.2BSD\n",
	    a_offset, a_size, a_size / 2048);
	printf("  sd0b: offset=%u size=%u (%u MB) type=swap\n",
	    b_offset, b_size, b_size / 2048);
	printf("  sd0c: offset=0 size=%u (%u MB) type=raw\n",
	    total_sectors, total_sectors / 2048);
	return 0;
}
