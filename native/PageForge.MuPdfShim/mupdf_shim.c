// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.
//
// PageForge.MuPdfShim â€” implementation over MuPDF (AGPLv3, Artifex Software).
// See mupdf_shim.h for the contract.

#include "mupdf/fitz.h"
#include "mupdf/pdf.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#include "mupdf_shim.h"

#ifdef _MSC_VER
#define PF_THREAD_LOCAL __declspec(thread)
#define PF_SNPRINTF _snprintf_s
#else
#define PF_THREAD_LOCAL __thread
#define PF_SNPRINTF snprintf
#endif

// Error string storage, one slot per thread so an in-flight error is never
// stomped by a concurrent call from another of the managed layer's workers.
static PF_THREAD_LOCAL char pf_error_buffer[512];

static void record_error(const char *message)
{
	if (message != NULL)
	{
		strncpy(pf_error_buffer, message, sizeof(pf_error_buffer) - 1);
		pf_error_buffer[sizeof(pf_error_buffer) - 1] = '\0';
	}
	else
	{
		pf_error_buffer[0] = '\0';
	}
}

static const char *caught_message(fz_context *ctx)
{
	record_error(fz_caught_message(ctx));
	return pf_error_buffer;
}

const char *pf_last_error(void)
{
	return pf_error_buffer;
}

int pf_create_context(pf_context *out_context, const char **out_error)
{
	fz_context *ctx;

	if (out_error != NULL)
	{
		*out_error = NULL;
	}

	if (out_context == NULL)
	{
		return PF_ERR;
	}

	*out_context = NULL;
	ctx = fz_new_context(NULL, NULL, FZ_STORE_UNLIMITED);
	if (ctx == NULL)
	{
		record_error("pf_create_context: out of memory creating fz_context");
		if (out_error != NULL)
		{
			*out_error = pf_error_buffer;
		}
		return PF_ERR;
	}

	fz_register_document_handlers(ctx);
	fz_try(ctx)
	{
		fz_set_aa_level(ctx, 8);
	}
	fz_catch(ctx)
	{
		record_error("pf_create_context: fz_set_aa_level failed");
		fz_drop_context(ctx);
		if (out_error != NULL)
		{
			*out_error = pf_error_buffer;
		}
		return PF_ERR;
	}

	*out_context = (pf_context)ctx;
	return PF_OK;
}

void pf_destroy_context(pf_context context)
{
	if (context != NULL)
	{
		fz_drop_context((fz_context *)context);
	}
}

int pf_open_document(pf_context context, const char *path_utf8, pf_document *out_document)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc;

	if (ctx == NULL || path_utf8 == NULL || out_document == NULL)
	{
		return PF_ERR;
	}

	*out_document = NULL;

	fz_var(doc);
	doc = NULL;

	fz_try(ctx)
	{
		doc = fz_open_document(ctx, path_utf8);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		return PF_ERR;
	}

	*out_document = (pf_document)doc;
	return PF_OK;
}

void pf_close_document(pf_context context, pf_document document)
{
	if (context != NULL && document != NULL)
	{
		fz_drop_document((fz_context *)context, (fz_document *)document);
	}
}

int pf_page_count(pf_context context, pf_document document, int *out_count)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;

	if (ctx == NULL || doc == NULL || out_count == NULL)
	{
		return PF_ERR;
	}

	fz_var(*out_count);
	*out_count = -1;

	fz_try(ctx)
	{
		*out_count = fz_count_pages(ctx, doc);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		return PF_ERR;
	}

	return PF_OK;
}

int pf_page_size(pf_context context, pf_document document, int page_index,
                 float *out_width_pt, float *out_height_pt)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	fz_page *page = NULL;
	fz_rect bounds;

	if (ctx == NULL || doc == NULL || out_width_pt == NULL || out_height_pt == NULL)
	{
		return PF_ERR;
	}

	*out_width_pt = 0.0f;
	*out_height_pt = 0.0f;

	fz_var(page);

	fz_try(ctx)
	{
		page = fz_load_page(ctx, doc, page_index);
		bounds = fz_bound_page(ctx, page);
		*out_width_pt = bounds.x1 - bounds.x0;
		*out_height_pt = bounds.y1 - bounds.y0;
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		if (page != NULL)
		{
			fz_drop_page(ctx, (fz_page *)page);
		}
		return PF_ERR;
	}

	fz_drop_page(ctx, (fz_page *)page);
	return PF_OK;
}

int pf_render_page_to_png(pf_context context, pf_document document, int page_index,
                          float dpi, const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	fz_page *page = NULL;
	fz_pixmap *pix = NULL;
	fz_matrix scale;

	if (ctx == NULL || doc == NULL || out_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	if (dpi < 1.0f)
	{
		dpi = 72.0f;
	}

	fz_var(page);
	fz_var(pix);

	fz_try(ctx)
	{
		page = fz_load_page(ctx, doc, page_index);
		scale = fz_scale(dpi / 72.0f, dpi / 72.0f);
		pix = fz_new_pixmap_from_page(ctx, page, scale, fz_device_rgb(ctx), 0);
		fz_save_pixmap_as_png(ctx, pix, out_path_utf8);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		if (pix != NULL)
		{
			fz_drop_pixmap(ctx, pix);
		}
		if (page != NULL)
		{
			fz_drop_page(ctx, (fz_page *)page);
		}
		return PF_ERR;
	}

	fz_drop_pixmap(ctx, pix);
	fz_drop_page(ctx, (fz_page *)page);
	return PF_OK;
}

int pf_page_text(pf_context context, pf_document document, int page_index,
                 const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	fz_page *page = NULL;
	fz_stext_page *stext = NULL;
	fz_buffer *buf = NULL;
	FILE *fh = NULL;

	if (ctx == NULL || doc == NULL || out_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	fz_var(page);
	fz_var(stext);
	fz_var(buf);

	fz_try(ctx)
	{
		page = fz_load_page(ctx, doc, page_index);
		stext = fz_new_stext_page_from_page(ctx, page, NULL);
		buf = fz_new_buffer_from_stext_page(ctx, stext);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		if (buf != NULL) fz_drop_buffer(ctx, buf);
		if (stext != NULL) fz_drop_stext_page(ctx, stext);
		if (page != NULL) fz_drop_page(ctx, (fz_page *)page);
		return PF_ERR;
	}

	fh = fopen(out_path_utf8, "wb");
	if (fh == NULL)
	{
		record_error("pf_page_text: cannot open output file");
		fz_drop_buffer(ctx, buf);
		fz_drop_stext_page(ctx, stext);
		fz_drop_page(ctx, (fz_page *)page);
		return PF_ERR;
	}
	fwrite(buf->data, 1, buf->len, fh);
	fclose(fh);

	fz_drop_buffer(ctx, buf);
	fz_drop_stext_page(ctx, stext);
	fz_drop_page(ctx, (fz_page *)page);
	return PF_OK;
}

static void write_outline_item(FILE *fh, int depth, int page, float x, float y,
                               const char *title)
{
	int i;
	if (title == NULL)
	{
		title = "";
	}
	for (i = 0; i < depth; ++i)
	{
		fputs("  ", fh);
	}
	fprintf(fh, "%d\t%d\t%g\t%g\t", depth + 1, page, (double)x, (double)y);
	for (; *title != '\0'; ++title)
	{
		char c = *title;
		if (c == '\t' || c == '\r' || c == '\n')
		{
			c = ' ';
		}
		fputc(c, fh);
	}
	fputc('\n', fh);
}

static void write_outline_tree(FILE *fh, fz_outline *node, int depth,
                               fz_context *ctx, fz_document *doc)
{
	while (node != NULL)
	{
		int page = 0;
		float x = 0.0f, y = 0.0f;
		int resolved = 1;

		fz_try(ctx)
		{
			fz_location loc = node->page;
			int n = fz_page_number_from_location(ctx, doc, loc);
			if (n >= 0)
			{
				page = n + 1;
			}
			else
			{
				page = 0;
			}
			x = (float)node->x;
			y = (float)node->y;
		}
		fz_catch(ctx)
		{
			(void)fz_caught(ctx);
			/* A single item's destination can be unresolvable; keep walking. */
			fz_warn(ctx, "pf_load_outline: resolving a destination failed");
			resolved = 0;
		}

		if (resolved)
		{
			write_outline_item(fh, depth, page, x, y, node->title);
		}

		if (node->down != NULL)
		{
			write_outline_tree(fh, node->down, depth + 1, ctx, doc);
		}
		node = node->next;
	}
}

int pf_load_outline(pf_context context, pf_document document, const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	fz_outline *outline = NULL;
	FILE *fh = NULL;

	if (ctx == NULL || doc == NULL || out_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	fz_var(outline);

	fz_try(ctx)
	{
		outline = fz_load_outline(ctx, doc);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		return PF_ERR;
	}

	if (outline == NULL)
	{
		return PF_OK;
	}

	fh = fopen(out_path_utf8, "wb");
	if (fh == NULL)
	{
		record_error("pf_load_outline: cannot open output file");
		fz_drop_outline(ctx, outline);
		return PF_ERR;
	}

	write_outline_tree(fh, outline, 0, ctx, doc);
	fclose(fh);

	fz_drop_outline(ctx, outline);
	return PF_OK;
}

/* ---- pf_build_pdf: FR-PAGE page-assembly primitive --------------------- */

typedef struct
{
	int id;
	char *path;
} pf_build_source;

typedef struct
{
	int src_id;
	int page;
	int rot; /* 0..3 = 0/90/180/270 CW */
} pf_build_page;

static void pf_build_job_free(char *buf, pf_build_source *srcs, int n_srcs,
                              pf_build_page *pages, int n_pages)
{
	int i;
	free(buf);
	if (srcs != NULL)
	{
		for (i = 0; i < n_srcs; ++i)
		{
			free(srcs[i].path);
		}
	}
	free(srcs);
	free(pages);
}

int pf_build_pdf(pf_context context, const char *job_path_utf8, const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	FILE *fh = NULL;
	char *buf = NULL;
	long fsize = 0;
	size_t rd = 0;
	const char *cursor, *line_end;
	int line_no = 0;
	int have_version = 0;
	int n_srcs = 0, src_cap = 0;
	int n_pages = 0, page_cap = 0;
	pf_build_source *srcs = NULL;
	pf_build_page *pages = NULL;
	pdf_document *dst = NULL;
	pdf_document **sources = NULL;
	pdf_graft_map **grafts = NULL;
	int n_sources = 0;
	pdf_write_options opts = pdf_default_write_options;
	int out_pages = 0;
	int status = PF_ERR;

	if (ctx == NULL || job_path_utf8 == NULL || out_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	fh = fopen(job_path_utf8, "rb");
	if (fh == NULL)
	{
		record_error("pf_build_pdf: cannot open the job file");
		return PF_ERR;
	}

	if (fseek(fh, 0, SEEK_END) != 0 || (fsize = ftell(fh)) < 0 || fseek(fh, 0, SEEK_SET) != 0)
	{
		record_error("pf_build_pdf: cannot size the job file");
		fclose(fh);
		return PF_ERR;
	}

	buf = (char *)malloc((size_t)fsize + 1);
	if (buf == NULL)
	{
		record_error("pf_build_pdf: out of memory reading the job file");
		fclose(fh);
		return PF_ERR;
	}
	rd = fread(buf, 1, (size_t)fsize, fh);
	buf[rd] = '\0';
	fclose(fh);

	cursor = buf;
	while (cursor != NULL && *cursor != '\0')
	{
		char type;
		line_end = strchr(cursor, '\n');
		{
			size_t len = line_end != NULL ? (size_t)(line_end - cursor) : strlen(cursor);
			char *line = (char *)malloc(len + 1);
			if (line == NULL)
			{
				record_error("pf_build_pdf: out of memory parsing the job file");
				goto cleanup;
			}
			memcpy(line, cursor, len);
			line[len] = '\0';
			if (len > 0 && line[len - 1] == '\r')
			{
				line[--len] = '\0';
			}
			cursor = line_end != NULL ? line_end + 1 : NULL;

			line_no++;
			type = line[0];

			if (type == 'V')
			{
				/* V<TAB>1  (version marker) */
				int v = strtol(line + 2, NULL, 10);
				if (have_version || v != 1)
				{
					free(line);
					record_error("pf_build_pdf: missing/duplicate/unknown version line");
					goto cleanup;
				}
				have_version = 1;
			}
			else if (type == 'S')
			{
				/* S<TAB><id><TAB><path> */
				int id;
				char *tab1, *tab2, *path;
				tab1 = strchr(line, '\t');
				if (tab1 == NULL)
					goto bad_line;
				tab2 = strchr(tab1 + 1, '\t');
				if (tab2 == NULL)
					goto bad_line;
				*tab2 = '\0';
				id = strtol(tab1 + 1, NULL, 10);
				path = tab2 + 1;
				if (id < 0)
					goto bad_line;
				if (n_srcs == src_cap)
				{
					int ncap = src_cap ? src_cap * 2 : 8;
					pf_build_source *ns = (pf_build_source *)realloc(
						srcs, (size_t)ncap * sizeof(*ns));
					if (ns == NULL)
					{
						free(line);
						record_error("pf_build_pdf: out of memory growing source table");
						goto cleanup;
					}
					srcs = ns;
					src_cap = ncap;
				}
				srcs[n_srcs].id = id;
				srcs[n_srcs].path = _strdup(path);
				if (srcs[n_srcs].path == NULL)
				{
					free(line);
					record_error("pf_build_pdf: out of memory duplicating source path");
					goto cleanup;
				}
				n_srcs++;
			}
			else if (type == 'P')
			{
				/* P<TAB><srcId><TAB><page0><TAB><rot> */
				int src_id = -1, page = 0, rot = 0;
				char *tab1, *tab2, *tab3;
				tab1 = strchr(line, '\t');
				if (tab1 == NULL)
					goto bad_line;
				tab2 = strchr(tab1 + 1, '\t');
				if (tab2 == NULL)
					goto bad_line;
				tab3 = strchr(tab2 + 1, '\t');
				if (tab3 == NULL)
					goto bad_line;
				*tab2 = '\0';
				src_id = strtol(tab1 + 1, NULL, 10);
				*tab3 = '\0';
				page = strtol(tab2 + 1, NULL, 10);
				rot = strtol(tab3 + 1, NULL, 10);
				if (src_id < 0 || page < 0)
					goto bad_line;
				if (n_pages == page_cap)
				{
					int ncap = page_cap ? page_cap * 2 : 64;
					pf_build_page *np = (pf_build_page *)realloc(
						pages, (size_t)ncap * sizeof(*np));
					if (np == NULL)
					{
						free(line);
						record_error("pf_build_pdf: out of memory growing page table");
						goto cleanup;
					}
					pages = np;
					page_cap = ncap;
				}
				pages[n_pages].src_id = src_id;
				pages[n_pages].page = page;
				pages[n_pages].rot = rot % 4;
				if (pages[n_pages].rot < 0)
					pages[n_pages].rot += 4;
				n_pages++;
			}
			else
			{
				/* unknown record type; leave tab-1 insertion incomplete */
			}
			free(line);
			continue;
		bad_line:
			free(line);
			{
				char msg[160];
				snprintf(msg, sizeof(msg), "pf_build_pdf: malformed job line %d", line_no);
				record_error(msg);
				goto cleanup;
			}
		}
	}

	if (!have_version || n_pages == 0)
	{
		record_error(have_version
			? "pf_build_pdf: job file contains no pages"
			: "pf_build_pdf: job file missing version line");
		goto cleanup;
	}

	/* Allocate the id-indexed source pointer tables. */
	{
		int max_id = -1;
		int i;
		for (i = 0; i < n_srcs; ++i)
		{
			if (srcs[i].id > max_id)
			{
				max_id = srcs[i].id;
			}
		}
		n_sources = max_id + 1;
		sources = (pdf_document **)calloc((size_t)n_sources, sizeof(*sources));
		grafts = (pdf_graft_map **)calloc((size_t)n_sources, sizeof(*grafts));
		if (sources == NULL || grafts == NULL)
		{
			record_error("pf_build_pdf: out of memory allocating source pointers");
			goto cleanup;
		}
	}

	fz_var(dst);
	fz_var(sources);
	fz_var(grafts);
	fz_var(out_pages);

	fz_try(ctx)
	{
		int i;
		dst = pdf_create_document(ctx);

		/* Open each registered source and build a graft map for it. */
		for (i = 0; i < n_srcs; ++i)
		{
			int id = srcs[i].id;
			sources[id] = pdf_open_document(ctx, srcs[i].path);
			grafts[id] = pdf_new_graft_map(ctx, dst);
		}

		/* Emit output pages in order, applying per-page rotation. */
		for (i = 0; i < n_pages; ++i)
		{
			pf_build_page *pg = &pages[i];
			if (pg->src_id < 0 || pg->src_id >= n_sources || sources[pg->src_id] == NULL)
			{
				char msg[160];
				snprintf(msg, sizeof(msg), "pf_build_pdf: source %d referenced but not registered", pg->src_id);
				record_error(msg);
				break;
			}
			pdf_graft_mapped_page(ctx, grafts[pg->src_id], -1, sources[pg->src_id], pg->page);
			if (pg->rot != 0)
			{
				pdf_obj *pageobj = pdf_lookup_page_obj(ctx, dst, pdf_count_pages(ctx, dst) - 1);
				if (pageobj != NULL)
				{
					int cur = pdf_dict_get_int(ctx, pageobj, PDF_NAME(Rotate));
					pdf_dict_put_int(ctx, pageobj, PDF_NAME(Rotate), (cur % 360 + pg->rot * 90) % 360);
				}
			}
			out_pages++;
		}

		if (out_pages != n_pages)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "%s", pf_error_buffer[0] ? pf_error_buffer : "pf_build_pdf: incomplete assembly");
		}

		pdf_save_document(ctx, dst, out_path_utf8, &opts);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		status = PF_ERR;
		goto cleanup;
	}

	status = PF_OK;

cleanup:
	if (sources != NULL)
	{
		int i;
		for (i = 0; i < n_sources; ++i)
		{
			if (sources[i] != NULL)
			{
				pdf_drop_document(ctx, sources[i]);
			}
		}
		free(sources);
	}
	if (grafts != NULL)
	{
		int i;
		for (i = 0; i < n_sources; ++i)
		{
			if (grafts[i] != NULL)
			{
				pdf_drop_graft_map(ctx, grafts[i]);
			}
		}
		free(grafts);
	}
	if (dst != NULL)
	{
		pdf_drop_document(ctx, dst);
	}
	pf_build_job_free(buf, srcs, n_srcs, pages, n_pages);
	return status;
}


/* ---- FR-ANNOT annotation primitives ------------------------------------- */

static pdf_document *as_pdf_document(fz_context *ctx, fz_document *doc)
{
	/* The shim only ever opens PDFs. */
	return pdf_specifics(ctx, doc);
}

/* Parse a single Tab-separated field starting at cursor. Terminates the
 * previous field in the line buffer in place (Tab -> NUL) and advances the
 * cursor past the next Tab or to NULL at end of input. Fills *field_start /
 * *field_len for the current field. Returns 0 when no more fields remain. */
static int next_field(char **cursor, char **field_start, size_t *field_len)
{
	char *line = *cursor;
	char *tab;
	if (line == NULL || *line == '\0')
	{
		return 0;
	}
	*field_start = line;
	tab = strchr(line, '\t');
	if (tab != NULL)
	{
		*tab = '\0';
		*field_len = (size_t)(tab - line);
		*cursor = tab + 1;
	}
	else
	{
		*field_len = strlen(line);
		*cursor = NULL;
	}
	return 1;
}

int pf_list_annotations(pf_context context, pf_document document, int page_index,
                        const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	pdf_page *page = NULL;
	pdf_annot *annot;
	FILE *fh = NULL;
	int status = PF_ERR;

	if (ctx == NULL || doc == NULL || out_path_utf8 == NULL || page_index < 0)
	{
		return PF_ERR;
	}

	fz_var(page);

	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_list_annotations: not a PDF document");
		}
		page = pdf_load_page(ctx, pdf, page_index);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		return PF_ERR;
	}

	fh = fopen(out_path_utf8, "wb");
	if (fh == NULL)
	{
		record_error("pf_list_annotations: cannot open output file");
		fz_drop_page(ctx, (fz_page *)page);
		return PF_ERR;
	}

	status = PF_OK;
	fz_var(annot);
	fz_try(ctx)
	{
		for (annot = pdf_first_annot(ctx, page); annot != NULL; annot = pdf_next_annot(ctx, annot))
		{
			enum pdf_annot_type atype = pdf_annot_type(ctx, annot);
			const char *type_name = pdf_string_from_annot_type(ctx, atype);
			fz_rect r = pdf_bound_annot(ctx, annot);
			const char *contents = pdf_annot_contents(ctx, annot);
			const char *c = contents != NULL ? contents : "";

			fprintf(fh, "%d\t%s\t%g\t%g\t%g\t%g\t",
				(int)atype, type_name != NULL ? type_name : "Unknown",
				(double)r.x0, (double)r.y0, (double)r.x1, (double)r.y1);
			for (; *c != '\0'; ++c)
			{
				char ch = *c;
				if (ch == '\t' || ch == '\r' || ch == '\n')
				{
					ch = ' ';
				}
				fputc(ch, fh);
			}
			fputc('\n', fh);
		}
	}
	fz_catch(ctx)
	{
		status = PF_ERR;
		caught_message(ctx);
	}

	fclose(fh);
	fz_drop_page(ctx, (fz_page *)page);
	return status;
}

int pf_add_annotation(pf_context context, pf_document document, int page_index,
                      const char *spec_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	pdf_page *page = NULL;
	pdf_annot *annot = NULL;
	enum pdf_annot_type type = PDF_ANNOT_UNKNOWN;
	fz_rect rect = { 0, 0, 0, 0 };
	int have_rect = 0;
	int quad_cap = 0, quad_count = 0;
	fz_quad *quads = NULL;
	int ink_cap = 0, ink_count = 0;
	fz_point *ink = NULL;
	int have_ink = 0;
	char *contents = NULL;
	float stroke[3] = { 0, 0, 0 };
	int have_stroke = 0;
	float opacity = -1.0f;
	char *buf = NULL;
	FILE *fh = NULL;
	long fsize = 0;
	char *cursor;
	int line_no = 0;
	int status = PF_ERR;

	if (ctx == NULL || doc == NULL || spec_path_utf8 == NULL || page_index < 0)
	{
		return PF_ERR;
	}

	fh = fopen(spec_path_utf8, "rb");
	if (fh == NULL)
	{
		record_error("pf_add_annotation: cannot open the spec file");
		return PF_ERR;
	}

	if (fseek(fh, 0, SEEK_END) != 0 || (fsize = ftell(fh)) < 0 || fseek(fh, 0, SEEK_SET) != 0)
	{
		record_error("pf_add_annotation: cannot size the spec file");
		fclose(fh);
		return PF_ERR;
	}

	buf = (char *)malloc((size_t)fsize + 1);
	if (buf == NULL)
	{
		record_error("pf_add_annotation: out of memory reading the spec file");
		fclose(fh);
		return PF_ERR;
	}
	{
		size_t rd = fread(buf, 1, (size_t)fsize, fh);
		buf[rd] = '\0';
	}
	fclose(fh);

	fz_var(pdf);
	fz_var(page);
	fz_var(annot);
	fz_var(quads);
	fz_var(ink);

	cursor = buf;
	while (cursor != NULL && *cursor != '\0')
	{
		char *tab;
		char rec_type;
		char *line;

		tab = strchr(cursor, '\n');
		if (tab != NULL)
		{
			*tab = '\0';
			if (tab > cursor && *(tab - 1) == '\r')
			{
				*(tab - 1) = '\0';
			}
		}
		line = cursor;
		cursor = tab != NULL ? tab + 1 : NULL;

		line_no++;
		rec_type = line[0];

		if (rec_type == 'T')
		{
			char *t = line + 1;
			if (*t == '\t')
			{
				t++;
			}
			type = pdf_annot_type_from_string(ctx, t);
			if (type == PDF_ANNOT_UNKNOWN)
			{
				char msg[160];
				snprintf(msg, sizeof(msg), "pf_add_annotation: unknown annotation type on line %d", line_no);
				record_error(msg);
				goto cleanup;
			}
		}
		else if (rec_type == 'R')
		{
			char *r = line + 1;
			char *f1, *f2, *f3, *f4;
			size_t l1, l2, l3, l4;
			if (!next_field(&r, &f1, &l1) || !next_field(&r, &f2, &l2) ||
				!next_field(&r, &f3, &l3) || !next_field(&r, &f4, &l4))
			{
				char msg[160];
				snprintf(msg, sizeof(msg), "pf_add_annotation: malformed Rect on line %d", line_no);
				record_error(msg);
				goto cleanup;
			}
			rect.x0 = (float)strtod(f1, NULL);
			rect.y0 = (float)strtod(f2, NULL);
			rect.x1 = (float)strtod(f3, NULL);
			rect.y1 = (float)strtod(f4, NULL);
			have_rect = 1;
		}
		else if (rec_type == 'C')
		{
			const char *c = line + 1;
			size_t n;
			if (*c == '\t')
			{
				c++;
			}
			n = strlen(c);
			contents = (char *)malloc(n + 1);
			if (contents == NULL)
			{
				record_error("pf_add_annotation: out of memory copying contents");
				goto cleanup;
			}
			memcpy(contents, c, n + 1);
		}
		else if (rec_type == 'Q')
		{
			char *q = line + 1;
			char *f1, *f2, *f3, *f4;
			size_t l1, l2, l3, l4;
			fz_quad quad;
			fz_quad *nq;
			if (!next_field(&q, &f1, &l1) || !next_field(&q, &f2, &l2) ||
				!next_field(&q, &f3, &l3) || !next_field(&q, &f4, &l4))
			{
				char msg[160];
				snprintf(msg, sizeof(msg), "pf_add_annotation: malformed Quad on line %d", line_no);
				record_error(msg);
				goto cleanup;
			}
			quad.ll = fz_make_point((float)strtod(f1, NULL), (float)strtod(f2, NULL));
			quad.lr = fz_make_point((float)strtod(f3, NULL), (float)strtod(f2, NULL));
			quad.ur = fz_make_point((float)strtod(f3, NULL), (float)strtod(f4, NULL));
			quad.ul = fz_make_point((float)strtod(f1, NULL), (float)strtod(f4, NULL));
			if (quad_count == quad_cap)
			{
				int ncap = quad_cap ? quad_cap * 2 : 8;
				nq = (fz_quad *)realloc(quads, (size_t)ncap * sizeof(*nq));
				if (nq == NULL)
				{
					record_error("pf_add_annotation: out of memory growing quad list");
					goto cleanup;
				}
				quads = nq;
				quad_cap = ncap;
			}
			quads[quad_count++] = quad;
		}
		else if (rec_type == 'I')
		{
			char *v = line + 1;
			char *f1, *f2;
			size_t l1, l2;
			fz_point p;
			fz_point *np;
			if (!next_field(&v, &f1, &l1) || !next_field(&v, &f2, &l2))
			{
				char msg[160];
				snprintf(msg, sizeof(msg), "pf_add_annotation: malformed Ink vertex on line %d", line_no);
				record_error(msg);
				goto cleanup;
			}
			p = fz_make_point((float)strtod(f1, NULL), (float)strtod(f2, NULL));
			if (ink_count == ink_cap)
			{
				int ncap = ink_cap ? ink_cap * 2 : 8;
				np = (fz_point *)realloc(ink, (size_t)ncap * sizeof(*np));
				if (np == NULL)
				{
					record_error("pf_add_annotation: out of memory growing ink list");
					goto cleanup;
				}
				ink = np;
				ink_cap = ncap;
			}
			ink[ink_count++] = p;
			have_ink = 1;
		}
		else if (rec_type == 'O')
		{
			char *o = line + 1;
			char *f1, *f2, *f3;
			size_t l1, l2, l3;
			if (!next_field(&o, &f1, &l1) || !next_field(&o, &f2, &l2) || !next_field(&o, &f3, &l3))
			{
				char msg[160];
				snprintf(msg, sizeof(msg), "pf_add_annotation: malformed color on line %d", line_no);
				record_error(msg);
				goto cleanup;
			}
			stroke[0] = (float)strtod(f1, NULL);
			stroke[1] = (float)strtod(f2, NULL);
			stroke[2] = (float)strtod(f3, NULL);
			have_stroke = 1;
		}
		else if (rec_type == 'P')
		{
			char *p = line + 1;
			if (*p == '\t')
			{
				p++;
			}
			opacity = (float)strtod(p, NULL);
		}
		/* unknown record type: ignored */
	}

	if (!have_rect || type == PDF_ANNOT_UNKNOWN)
	{
		record_error(have_rect
			? "pf_add_annotation: annotation type missing"
			: "pf_add_annotation: Rect missing");
		goto cleanup;
	}

	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_add_annotation: not a PDF document");
		}
		page = pdf_load_page(ctx, pdf, page_index);
		annot = pdf_create_annot(ctx, page, type);

		/* Highlight/underline/strikethrough/ink rectangles are defined by their
		 * quad points or ink vertices, not by a /Rect entry, so pdf_set_annot_rect
		 * would reject them. MuPDF derives the rect from that data below; every
		 * other type sets it directly. */
		if (type != PDF_ANNOT_HIGHLIGHT &&
			type != PDF_ANNOT_UNDERLINE &&
			type != PDF_ANNOT_STRIKE_OUT &&
			type != PDF_ANNOT_INK)
		{
			pdf_set_annot_rect(ctx, annot, rect);
		}

		if (have_stroke)
		{
			pdf_set_annot_color(ctx, annot, 3, stroke);
		}
		if (opacity >= 0.0f && opacity <= 1.0f)
		{
			pdf_set_annot_opacity(ctx, annot, opacity);
		}

		switch (type)
		{
		case PDF_ANNOT_HIGHLIGHT:
		case PDF_ANNOT_UNDERLINE:
		case PDF_ANNOT_STRIKE_OUT:
			if (quad_count > 0)
			{
				pdf_set_annot_quad_points(ctx, annot, quad_count, quads);
			}
			break;
		case PDF_ANNOT_INK:
			if (have_ink && ink_count > 0)
			{
				int counts[1] = { ink_count };
				pdf_set_annot_ink_list(ctx, annot, 1, counts, ink);
			}
			break;
		default:
			break;
		}

		if (contents != NULL)
		{
			pdf_set_annot_contents(ctx, annot, contents);
		}

		pdf_update_annot(ctx, annot);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		status = PF_ERR;
		goto cleanup;
	}

	status = PF_OK;

cleanup:
	free(buf);
	free(contents);
	free(quads);
	free(ink);
	if (page != NULL)
	{
		fz_drop_page(ctx, (fz_page *)page);
	}
	return status;
}

/* Find an unused XObject name in the page's resources (starting fresh each
 * time so flattening multiple annotations never collides). Returns a temp
 * static buffer; the caller must copy the name if used beyond the next call. */
static pdf_obj *unused_xobject_name(fz_context *ctx, pdf_document *doc, pdf_obj *resources, int *seed)
{
	pdf_obj *xobjects;
	pdf_obj *name;
	char nbuf[32];

	xobjects = pdf_dict_get(ctx, resources, PDF_NAME(XObject));
	if (xobjects == NULL)
	{
		xobjects = pdf_new_dict(ctx, doc, 2);
		pdf_dict_put(ctx, resources, PDF_NAME(XObject), xobjects);
	}

	for (;;)
	{
		(*seed)++;
		snprintf(nbuf, sizeof(nbuf), "Fr%d", *seed);
		name = pdf_new_name(ctx, nbuf);
		if (pdf_dict_get(ctx, xobjects, name) == NULL)
		{
			return name;
		}
	}
}

/* Embed one annotation's synthesized appearance into the page's content
 * streams, then remove the annotation. The appearance Form XObject is added to
 * the page /Resources and invoked with the matrix pdf_annot_transform yields --
 * the same transform MuPDF uses to paint the annotation -- so the flattened
 * page renders identically to the annotated page while the annotation is gone. */
static void flatten_one_annotation(fz_context *ctx, pdf_document *doc,
                                   pdf_page *page, int page_index, pdf_annot *annot)
{
	static int seed = 0;
	pdf_obj *pageobj = pdf_lookup_page_obj(ctx, doc, page_index);
	pdf_obj *resources = pdf_dict_get_inheritable(ctx, pageobj, PDF_NAME(Resources));
	pdf_obj *xobject_name;
	pdf_obj *appearance;
	pdf_obj *ap = pdf_annot_ap(ctx, annot);
	pdf_obj *contents;
	fz_matrix m;
	char content[256];
	int len;

	if (ap == NULL)
	{
		/* No appearance stream: nothing to embed, just remove the annotation. */
		pdf_delete_annot(ctx, page, annot);
		return;
	}

	if (resources == NULL)
	{
		resources = pdf_new_dict(ctx, doc, 2);
		pdf_dict_put(ctx, pageobj, PDF_NAME(Resources), resources);
	}

	m = pdf_annot_transform(ctx, annot);
	xobject_name = unused_xobject_name(ctx, doc, resources, &seed);

	/* Deep-copy the appearance so it exists independently in the output. */
	appearance = pdf_deep_copy_obj(ctx, ap);
	pdf_dict_put(ctx, pdf_dict_get(ctx, resources, PDF_NAME(XObject)), xobject_name, appearance);

	len = snprintf(content, sizeof(content),
		"q %g %g %g %g %g %g /%s Do Q\n",
		(double)m.a, (double)m.b, (double)m.c, (double)m.d, (double)m.e, (double)m.f,
		pdf_to_name(ctx, xobject_name));

	/* Append a new content stream invoking the appearance. */
	{
		pdf_obj *stream = pdf_add_new_dict(ctx, doc, 1);
		fz_buffer *buf = fz_new_buffer_from_copied_data(ctx, (const unsigned char *)content, (size_t)len);
		pdf_update_stream(ctx, doc, stream, buf, 0);

		contents = pdf_dict_get(ctx, pageobj, PDF_NAME(Contents));
		if (pdf_is_array(ctx, contents))
		{
			pdf_array_push(ctx, contents, stream);
		}
		else if (contents != NULL)
		{
			/* Single stream: promote to an array [old new]. */
			pdf_obj *arr = pdf_new_array(ctx, doc, 2);
			pdf_array_push(ctx, arr, contents);
			pdf_array_push(ctx, arr, stream);
			pdf_dict_put(ctx, pageobj, PDF_NAME(Contents), arr);
		}
		else
		{
			pdf_dict_put(ctx, pageobj, PDF_NAME(Contents), stream);
		}
	}

	pdf_delete_annot(ctx, page, annot);
}

/* True when the annotation's type name is one of the comma-separated names in
 * types_utf8 (or the list is empty, meaning "all non-link types"). Used to make
 * flatten-on-export selectable per annotation type (FR-ANNOT-02). */
static int annot_type_in_list(fz_context *ctx, pdf_annot *annot, const char *types_utf8)
{
	const char *type_name;
	const char *p;
	size_t n;

	if (types_utf8 == NULL || types_utf8[0] == '\0')
	{
		return 1;
	}

	type_name = pdf_string_from_annot_type(ctx, pdf_annot_type(ctx, annot));
	if (type_name == NULL)
	{
		return 0;
	}
	n = strlen(type_name);

	for (p = types_utf8; *p != '\0';)
	{
		const char *comma = strchr(p, ',');
		size_t len = comma != NULL ? (size_t)(comma - p) : strlen(p);
		while (len > 0 && p[len - 1] == ' ')
		{
			len--;
		}
		while (*p == ' ')
		{
			p++;
			len--;
		}
		if (len == n && strncmp(p, type_name, n) == 0)
		{
			return 1;
		}
		p = comma != NULL ? comma + 1 : p + len;
	}

	return 0;
}

int pf_flatten_annotations(pf_context context, pf_document document, int page_index,
                           const char *types_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	pdf_page *page = NULL;
	pdf_annot *annot;
	int status = PF_ERR;

	if (ctx == NULL || doc == NULL || page_index < 0)
	{
		return PF_ERR;
	}

	fz_var(pdf);
	fz_var(page);

	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_flatten_annotations: not a PDF document");
		}
		page = pdf_load_page(ctx, pdf, page_index);

		annot = pdf_first_annot(ctx, page);
		while (annot != NULL)
		{
			pdf_annot *next = pdf_next_annot(ctx, annot);
			if (pdf_annot_type(ctx, annot) != PDF_ANNOT_LINK &&
				annot_type_in_list(ctx, annot, types_utf8))
			{
				flatten_one_annotation(ctx, pdf, page, page_index, annot);
			}
			annot = next;
		}
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		status = PF_ERR;
		if (page != NULL)
		{
			fz_drop_page(ctx, (fz_page *)page);
		}
		return status;
	}

	if (page != NULL)
	{
		fz_drop_page(ctx, (fz_page *)page);
	}
	return PF_OK;
}

int pf_save_document(pf_context context, pf_document document, const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	pdf_write_options opts = pdf_default_write_options;
	int status = PF_ERR;

	if (ctx == NULL || doc == NULL || out_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	fz_var(pdf);

	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_save_document: not a PDF document");
		}
pdf_save_document(ctx, pdf, out_path_utf8, &opts);
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		return PF_ERR;
	}

	status = PF_OK;
	return status;
}

/* ---- FR-EDIT text-run primitives --------------------------------------- */

/* The rewrite primitive splices the raw "string operator" bytes of one text
 * run's showing op(s) in the page's content stream (Tj, ' or TJ) and replaces
 * them with a single freshly-escaped "(...) Tj". Run/op matching is
 * geometry-correlated to the same structured-text walk that drives hit
 * testing (FR-EDIT-01): it compares the op's pen origin in device space with
 * the run's first-character origin, plus the decoded byte text. The new text's
 * encodability into the run's font is a 2B-depth slice of FR-EDIT-03; deep
 * glyph/subset checking lands in a later slice. Undo/redo (FR-EDIT-05) splice
 * the receipt's stored old/new operator bytes back in place. */

#define PF_TEXT_OP_TJ       0
#define PF_TEXT_OP_APOST    1
#define PF_TEXT_OP_ARR      2

/* FR-EDIT-04: an image/vector object invocation (`cm ... Do`). */
#define PF_OBJ_OP_DO        3
#define PF_OBJ_TAG_IMAGE    1
#define PF_OBJ_TAG_FORM     2

#define PF_MAX_TOKEN_NAME   256
#define PF_MAX_NUM          16
#define PF_MAX_FONTS        8
#define PF_MAX_CTM_DEPTH    64
#define PF_STRING_CAP       (1 << 20)
#define PF_ORIGIN_TOLERANCE 0.75f

typedef struct pf_dynbuf_s
{
	unsigned char *data;
	size_t len, cap;
} pf_dynbuf_s;

static int pf_dynbuf_push(pf_dynbuf_s *b, const void *p, size_t n)
{
	if (b->len + n > b->cap)
	{
		size_t ncap = b->cap ? b->cap * 2 : 1024;
		unsigned char *nd;
		while (ncap < b->len + n)
		{
			ncap *= 2;
		}
		nd = (unsigned char *)realloc(b->data, ncap);
		if (nd == NULL)
		{
			return 0;
		}
		b->data = nd;
		b->cap = ncap;
	}
	if (n > 0)
	{
		memcpy(b->data + b->len, p, n);
	}
	b->len += n;
	return 1;
}

static int pf_dynbuf_pushc(pf_dynbuf_s *b, unsigned char c)
{
	return pf_dynbuf_push(b, &c, 1);
}

static void pf_dynbuf_free(pf_dynbuf_s *b)
{
	free(b->data);
	b->data = NULL;
	b->len = b->cap = 0;
}

static int pf_is_delim(unsigned char c)
{
	return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' ||
	       c == '\0' || c == '%' || c == '(' || c == ')' || c == '[' ||
	       c == ']' || c == '<' || c == '>' || c == '{' || c == '}' || c == '/';
}

static int pf_hexval(unsigned char c)
{
	if (c >= '0' && c <= '9')
	{
		return c - '0';
	}
	if (c >= 'A' && c <= 'F')
	{
		return c - 'A' + 10;
	}
	if (c >= 'a' && c <= 'f')
	{
		return c - 'a' + 10;
	}
	return -1;
}

static float pf_fabsf(float x)
{
	return x < 0.0f ? -x : x;
}

/* Decodes a literal string starting at '(' into out (<= cap). Returns the
 * count of decoded bytes and leaves *pos on the byte after the ')' (or at end
 * of data when unterminated). */
static size_t pf_decode_literal(const unsigned char *data, size_t len, size_t *pos,
                                unsigned char *out, size_t cap)
{
	size_t p = *pos + 1;
	size_t n = 0;
	int depth = 0;
	while (p < len)
	{
		unsigned char c = data[p];
		if (c == ')' && depth == 0)
		{
			p++;
			break;
		}
		if (c == '(')
		{
			depth++;
			if (n < cap)
			{
				out[n++] = '(';
			}
			p++;
			continue;
		}
		if (c == ')')
		{
			depth--;
			if (n < cap)
			{
				out[n++] = ')';
			}
			p++;
			continue;
		}
		if (c == '\\')
		{
			p++;
			if (p >= len)
			{
				break;
			}
			c = data[p];
			if (c >= '0' && c <= '7')
			{
				unsigned v = 0;
				int k;
				for (k = 0; k < 3 && p < len && data[p] >= '0' && data[p] <= '7'; k++)
				{
					v = v * 8 + (unsigned)(data[p] - '0');
					p++;
				}
				if (n < cap)
				{
					out[n++] = (unsigned char)(v & 0xFF);
				}
			}
			else if (c == 'n')
			{
				p++;
				if (n < cap)
				{
					out[n++] = '\n';
				}
			}
			else if (c == 'r')
			{
				p++;
				if (n < cap)
				{
					out[n++] = '\r';
				}
			}
			else if (c == 't')
			{
				p++;
				if (n < cap)
				{
					out[n++] = '\t';
				}
			}
			else if (c == 'b')
			{
				p++;
				if (n < cap)
				{
					out[n++] = '\b';
				}
			}
			else if (c == 'f')
			{
				p++;
				if (n < cap)
				{
					out[n++] = '\f';
				}
			}
			else if (c == '\n')
			{
				p++;
			}
			else if (c == '\r')
			{
				p++;
				if (p < len && data[p] == '\n')
				{
					p++;
				}
			}
			else
			{
				p++;
				if (n < cap)
				{
					out[n++] = c;
				}
			}
			continue;
		}
		else if (c == '\n' || c == '\r')
		{
			if (n < cap)
			{
				out[n++] = '\n';
			}
			p++;
			if (c == '\r' && p < len && data[p] == '\n')
			{
				p++;
			}
		}
		else
		{
			if (n < cap)
			{
				out[n++] = c;
			}
			p++;
		}
	}
	*pos = p;
	return n;
}

static size_t pf_decode_hex(const unsigned char *data, size_t len, size_t *pos,
                            unsigned char *out, size_t cap)
{
	size_t p = *pos + 1;
	size_t n = 0;
	unsigned char hi = 0;
	int have = 0;
	while (p < len && data[p] != '>')
	{
		int hv = pf_hexval(data[p]);
		if (hv >= 0)
		{
			if (!have)
			{
				hi = (unsigned char)(hv << 4);
				have = 1;
			}
			else
			{
				if (n < cap)
				{
					out[n++] = (unsigned char)(hi | hv);
				}
				have = 0;
			}
		}
		p++;
	}
	if (have && n < cap)
	{
		out[n++] = hi;
	}
	if (p < len && data[p] == '>')
	{
		p++;
	}
	*pos = p;
	return n;
}

typedef struct pf_tok_s
{
	int kind;          /* 1 name, 2 number, 3 literal, 4 hex, 5 '[', 6 ']', 9 operator, 10 '<<' */
	size_t start, end; /* raw byte span in the stream */
	double num;        /* numeric value when kind == 2 */
	size_t dlen;       /* decoded string length when kind == 1/3/4/9 (sbuf) */
} pf_tok_s;

/* Advances *pos over whitespace/comments and the next token into tok. Decoded
 * name/string/operator content lands in sbuf (>= PF_STRING_CAP). Returns 0 at
 * end of data. */
static int pf_next_tok(const unsigned char *data, size_t len, size_t *pos,
                       pf_tok_s *tok, unsigned char *sbuf)
{
	size_t p = *pos;
	size_t guard = *pos;
	while (p < len)
	{
		unsigned char c = data[p];
		if (c == '%')
		{
			while (p < len && data[p] != '\n' && data[p] != '\r')
			{
				p++;
			}
		}
		else if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\0')
		{
			p++;
		}
		else
		{
			break;
		}
	}
	if (p >= len)
	{
		return 0;
	}

	tok->start = p;
	tok->num = 0.0;
	tok->dlen = 0;

	{
		unsigned char c = data[p];
		if (c == '(')
		{
			tok->kind = 3;
			tok->dlen = pf_decode_literal(data, len, &p, sbuf, PF_STRING_CAP);
			tok->end = p;
		}
		else if (c == '<' && p + 1 < len && data[p + 1] == '<')
		{
			tok->kind = 10;
			tok->end = p + 2;
			p += 2;
		}
		else if (c == '<')
		{
			tok->kind = 4;
			tok->dlen = pf_decode_hex(data, len, &p, sbuf, PF_STRING_CAP);
			tok->end = p;
		}
		else if (c == '/')
		{
			char tmp[PF_MAX_TOKEN_NAME];
			size_t n = 0, q = p + 1;
			while (q < len && !pf_is_delim(data[q]) && n + 1 < sizeof(tmp))
			{
				if (data[q] == '#' && q + 2 < len &&
				    pf_hexval(data[q + 1]) >= 0 && pf_hexval(data[q + 2]) >= 0)
				{
					tmp[n++] = (char)((pf_hexval(data[q + 1]) << 4) | pf_hexval(data[q + 2]));
					q += 3;
				}
				else
				{
					tmp[n++] = (char)data[q];
					q++;
				}
			}
			tmp[n] = '\0';
			memcpy(sbuf, tmp, n + 1);
			tok->kind = 1;
			tok->dlen = n;
			tok->end = q;
			p = q;
		}
		else if (c == '[')
		{
			tok->kind = 5;
			tok->end = p + 1;
			p++;
		}
		else if (c == ']')
		{
			tok->kind = 6;
			tok->end = p + 1;
			p++;
		}
		else if (c == '{' || c == '}')
		{
			tok->kind = 7;
			tok->end = p + 1;
			p++;
		}
		else if (c == '+' || c == '-' || c == '.' || (c >= '0' && c <= '9'))
		{
			char tmp[64];
			size_t n = 0, q = p;
			while (q < len && !pf_is_delim(data[q]) && n + 1 < sizeof(tmp))
			{
				tmp[n++] = (char)data[q];
				q++;
			}
			tmp[n] = '\0';
			tok->num = strtod(tmp, NULL);
			tok->kind = 2;
			tok->end = q;
			p = q;
		}
		else
		{
			char tmp[16];
			size_t n = 0, q = p;
			while (q < len && !pf_is_delim(data[q]) && n + 1 < sizeof(tmp))
			{
				tmp[n++] = (char)data[q];
				q++;
			}
			tmp[n] = '\0';
			memcpy(sbuf, tmp, n + 1);
			tok->kind = 9;
			tok->dlen = n;
			tok->end = q;
			p = q;
		}
	}

	if (p < *pos || p == guard)
	{
		/* Forward-progress guarantee: never leave *pos unmoved, so an
		 * unexpected delimiter byte (e.g. a stray ')' or '>') cannot make the
		 * content walker spin forever. Skip a single raw byte. */
		p = guard + 1;
	}

	*pos = p;
	return 1;
}

typedef struct pf_text_op_s
{
	int stream_index;
	size_t span_start, span_end; /* raw byte range inside the stream to replace */
	int kind;                    /* PF_TEXT_OP_TJ / APOST / ARR */
	char font_res[PF_MAX_TOKEN_NAME];
	float font_size;
	unsigned char *bytes;        /* flattened decoded glyph bytes */
	size_t nbytes;
	int *adj_pos;                /* byte index before which a TJ adjust applies */
	float *adj_val;
	size_t nadj;
	float tc, tw, tz;
	fz_matrix tm, ctm;
	float origin_x, origin_y;
	fz_rect bbox;
	int geom_ok;
	char *utext;                 /* decoded text (UTF-8), for matching/errors */
	/* FR-EDIT-04 object invocation (kind == PF_OBJ_OP_DO): metadata to list and
	 * the raw `cm ... Do` placeholder bytes to splice for move/resize. */
	fz_matrix obj_ctm;           /* device CTM at the object's Do */
	char obj_name[PF_MAX_TOKEN_NAME];
	int obj_tag;                 /* PF_OBJ_TAG_IMAGE / PF_OBJ_TAG_FORM */
	unsigned char *obj_bytes;    /* raw placeholder bytes (`cm ... Do`) to splice */
	size_t obj_nbytes;
	int obj_has_cm;              /* 1 when a cm directly precedes the Do */
	float obj_w, obj_h;          /* intrinsic size of the XObject (1x1 for forms) */
} pf_text_op_s;

static int pf_opspush(pf_text_op_s **ops, int *n, int *cap)
{
	int ncap;
	pf_text_op_s *p;
	if (*n < *cap)
	{
		return 1;
	}
	ncap = *cap ? *cap * 2 : 32;
	p = (pf_text_op_s *)realloc(*ops, (size_t)ncap * sizeof(pf_text_op_s));
	if (p == NULL)
	{
		return 0;
	}
	*ops = p;
	*cap = ncap;
	return 1;
}

static int pf_opbytes(pf_text_op_s *op, const unsigned char *b, size_t n)
{
	unsigned char *p;
	if (n == 0)
	{
		return 1;
	}
	p = (unsigned char *)realloc(op->bytes, op->nbytes + n);
	if (p == NULL)
	{
		return 0;
	}
	memcpy(p + op->nbytes, b, n);
	op->bytes = p;
	op->nbytes += n;
	return 1;
}

static int pf_opadj(pf_text_op_s *op, int before, float val)
{
	int *p = (int *)realloc(op->adj_pos, (op->nadj + 1) * sizeof(int));
	float *q;
	if (p == NULL)
	{
		return 0;
	}
	op->adj_pos = p;
	q = (float *)realloc(op->adj_val, (op->nadj + 1) * sizeof(float));
	if (q == NULL)
	{
		return 0;
	}
	op->adj_val = q;
	op->adj_pos[op->nadj] = before;
	op->adj_val[op->nadj] = val;
	op->nadj++;
	return 1;
}

static void pf_free_text_ops(pf_text_op_s *ops, int n)
{
	int i;
	for (i = 0; i < n; i++)
	{
		free(ops[i].bytes);
		free(ops[i].adj_pos);
		free(ops[i].adj_val);
		free(ops[i].utext);
	}
	free(ops);
}

/* Numeric history ring used by the content-walker text-state emulation.
 * r->v[0] is the OLDEST of the numbers pushed since the previous operator. */
typedef struct pf_numring_s
{
	double v[PF_MAX_NUM];
	int count;
} pf_numring_s;

static void pf_ring_push(pf_numring_s *r, double x)
{
	if (r->count == PF_MAX_NUM)
	{
		size_t i;
		for (i = 0; i + 1 < (size_t)PF_MAX_NUM; i++)
		{
			r->v[i] = r->v[i + 1];
		}
		r->v[PF_MAX_NUM - 1] = x;
	}
	else
	{
		r->v[r->count++] = x;
	}
}

static double pf_ring_at(pf_numring_s *r, int i)
{
	if (i < 0 || i >= r->count)
	{
		return 0.0;
	}
	return r->v[i];
}

static void pf_ring_clear(pf_numring_s *r)
{
	r->count = 0;
}

typedef struct pf_fontcache_s
{
	pdf_font_desc *desc[PF_MAX_FONTS];
	char name[PF_MAX_FONTS][PF_MAX_TOKEN_NAME];
	int n;
} pf_fontcache_s;

static void pf_fontcache_free(fz_context *ctx, pf_fontcache_s *c)
{
	int i;
	for (i = 0; i < c->n; i++)
	{
		if (c->desc[i] != NULL)
		{
			pdf_drop_font(ctx, c->desc[i]);
		}
		c->desc[i] = NULL;
	}
	c->n = 0;
}

static pdf_font_desc *pf_resolve_font(fz_context *ctx, pdf_document *pdf,
                                      pdf_obj *resources, const char *resname,
                                      pf_fontcache_s *c)
{
	pdf_obj *fonts, *fontobj, *fname;
	pdf_font_desc *fd;
	int i;
	if (resname == NULL || resname[0] == '\0')
	{
		return NULL;
	}
	for (i = 0; i < c->n; i++)
	{
		if (strcmp(c->name[i], resname) == 0)
		{
			return c->desc[i];
		}
	}
	fonts = pdf_dict_get(ctx, resources, PDF_NAME(Font));
	if (fonts == NULL)
	{
		return NULL;
	}
	fname = pdf_new_name(ctx, resname);
	fontobj = pdf_dict_get(ctx, fonts, fname);
	pdf_drop_obj(ctx, fname);
	if (fontobj == NULL)
	{
		return NULL;
	}
	fd = pdf_load_font(ctx, pdf, NULL, fontobj);
	if (fd == NULL || fd->font == NULL)
	{
		return NULL;
	}
	if (c->n == PF_MAX_FONTS)
	{
		if (c->desc[0] != NULL)
		{
			pdf_drop_font(ctx, c->desc[0]);
		}
		c->n--;
		for (i = 0; i < c->n; i++)
		{
			c->desc[i] = c->desc[i + 1];
			strcpy(c->name[i], c->name[i + 1]);
		}
	}
	c->desc[c->n] = fd;
	strcpy(c->name[c->n], resname);
	c->n++;
	return fd;
}

static int pf_append_utf8(pf_dynbuf_s *b, int rune)
{
	char tmp[6];
	int n;
	if (rune <= 0)
	{
		return 1;
	}
	n = fz_runetochar(tmp, rune);
	if (n <= 0)
	{
		return 1;
	}
	return pf_dynbuf_push(b, tmp, (size_t)n);
}

/* Lays out op's decoded glyph bytes against op->tm/ctm and the active text
 * state, filling in origin, bbox and utext. Self-contained: font resolution
 * and every fz_* call run under a nested try/catch so a broken font only
 * costs geometry for that one op instead of failing the whole walk. */
static int pf_op_geometry(fz_context *ctx, pdf_document *pdf, pdf_obj *resources,
                          pf_text_op_s *op, pf_fontcache_s *fc)
{
	pdf_font_desc *fdesc = NULL;
	fz_font *font;
	fz_text *text = NULL;
	fz_matrix base, emgtm, ggtm;
	fz_point o;
	double pen = 0.0;
	size_t i, ai = 0;
	int gok = 0;
	unsigned short inv[256];
	int j;

	op->geom_ok = 0;
	free(op->utext);
	op->utext = NULL;
	if (op->nbytes == 0 || op->font_res[0] == '\0')
	{
		return 0;
	}

	for (j = 0; j < 256; j++)
	{
		unsigned short ucs = fz_unicode_from_pdf_doc_encoding[j];
		inv[j] = (ucs == 0) ? (unsigned short)(j < 0x80 ? j : '?') : ucs;
	}

	fz_try(ctx)
	{
		fdesc = pf_resolve_font(ctx, pdf, resources, op->font_res, fc);
		if (fdesc == NULL)
		{
			gok = -1;
		}
		if (gok == 0)
		{
			font = fdesc->font;
			base = fz_concat(fz_translate(op->tm.e, op->tm.f), op->ctm);
			o = fz_transform_point(fz_make_point(0, 0), base);
			op->origin_x = o.x;
			op->origin_y = o.y;
			emgtm = fz_concat(fz_scale(op->font_size, op->font_size), base);
			text = fz_new_text(ctx);
			pen = 0.0;
			for (i = 0; i < op->nbytes; i++)
			{
				unsigned char byte = op->bytes[i];
				int ucs = (int)inv[byte];
				int gid;
				double adv;
				while (ai < op->nadj && op->adj_pos[ai] == (int)i)
				{
					pen -= (double)op->adj_val[ai] / 1000.0;
					ai++;
				}
				gid = fz_encode_character(ctx, font, ucs);
				ggtm = fz_pre_translate(emgtm, (float)pen, 0);
				if (gid > 0)
				{
					fz_show_glyph(ctx, text, font, ggtm, gid, ucs, 0, 0,
					              FZ_BIDI_LTR, FZ_LANG_UNSET);
					adv = fz_advance_glyph(ctx, font, gid, 0);
				}
				else
				{
					adv = 0.0;
				}
				adv += op->tc / 1000.0 + (ucs == 0x20 ? op->tw / 1000.0 : 0.0);
				adv *= op->tz / 100.0;
				pen += adv;
			}
			op->bbox = fz_bound_text(ctx, text, NULL, fz_identity);
			fz_drop_text(ctx, text);
			text = NULL;
			gok = 1;
		}
	}
	fz_catch(ctx)
	{
		if (text != NULL)
		{
			fz_drop_text(ctx, text);
		}
		gok = 0;
	}
	if (gok != 1)
	{
		op->geom_ok = 0;
		return 0;
	}
	op->geom_ok = 1;
	{
		pf_dynbuf_s ub = { NULL, 0, 0 };
		int ok = 1;
		char *u = NULL;
		for (i = 0; i < op->nbytes && ok; i++)
		{
			unsigned short ucs = inv[op->bytes[i]];
			if (ucs != 0 && ucs != 0xFFFD)
			{
				ok = pf_append_utf8(&ub, (int)ucs);
			}
		}
		if (ok)
		{
			u = (char *)malloc(ub.len + 1);
			if (u != NULL)
			{
				memcpy(u, ub.data, ub.len);
				u[ub.len] = '\0';
			}
			if (u != NULL)
			{
				op->utext = u;
			}
		}
		pf_dynbuf_free(&ub);
	}
	return op->geom_ok;
}

/* ---- run listing -------------------------------------------------------- */

typedef struct pf_run_s
{
	float x0, y0, x1, y1;
	float origin_x, origin_y;
	float size;
	fz_font *font;   /* owned by the stext page, not by us */
	char *utext;
} pf_run_s;

static void pf_free_runs(pf_run_s *runs, int n)
{
	int i;
	if (runs == NULL)
	{
		return;
	}
	for (i = 0; i < n; i++)
	{
		free(runs[i].utext);
	}
	free(runs);
}

static char *pf_build_run_text(fz_stext_line *line, fz_font *font, float size,
                               int *oom)
{
	pf_dynbuf_s ub = { NULL, 0, 0 };
	fz_stext_char *cc;
	char *u = NULL;
	for (cc = line->first_char; cc != NULL && !*oom; cc = cc->next)
	{
		if (cc->font != font || pf_fabsf(cc->size - size) > 0.001f)
		{
			continue;
		}
		if (cc->c != 0 && cc->c != 0xFFFD)
		{
			if (!pf_append_utf8(&ub, cc->c))
			{
				*oom = 1;
			}
		}
	}
	if (!*oom)
	{
		u = (char *)malloc(ub.len + 1);
		if (u != NULL)
		{
			memcpy(u, ub.data, ub.len);
			u[ub.len] = '\0';
		}
		else
		{
			*oom = 1;
		}
	}
	pf_dynbuf_free(&ub);
	return u;
}

/* Builds the sorted (block > line > run) run list for a page from the same
 * structured-text walk the hit test uses, and keeps the stext page alive for
 * the caller (run->font pointers belong to it). Returns PF_OK, or -1 on
 * allocation failure, or PF_ERR on a MuPDF error. */
/* Inverse of the page transform's Y axis used by the walker: converts a MuPDF
 * device-space Y (top-left origin, y down) to PDF space (bottom-left, y up). */
#define PF_FLIPY(h, y) ((h) > 0.0f ? ((h) - (y)) : (y))

static int pf_build_runs(fz_context *ctx, fz_document *doc, int page_index,
                         pf_run_s **out, int *nout, fz_stext_page **out_stext)
{
	fz_page *page = NULL;
	fz_stext_page *stext = NULL;
	pf_run_s *runs = NULL;
	int n = 0, cap = 0;
	fz_stext_block *block;
	int oom = 0;
	int status = PF_ERR;
	float page_h = 0.0f;

	*out = NULL;
	*nout = 0;
	*out_stext = NULL;

	fz_var(page);
	fz_var(stext);
	fz_var(runs);

	fz_try(ctx)
	{
		page = fz_load_page(ctx, doc, page_index);
		stext = fz_new_stext_page_from_page(ctx, page, NULL);
		if (stext == NULL)
		{
			status = PF_OK; /* stext never returns NULL on success paths */
		}
		/* MuPDF stext reports char origins in device space (top-left origin,
		 * y increasing downward), while the content walker's op geometry is in
		 * PDF space (bottom-left origin, y increasing upward). Flip the Y axis
		 * over the page height so run and op origins share one coordinate
		 * space for FR-EDIT-01 hit-test matching. */
		{
			fz_rect pr = fz_bound_page(ctx, page);
			page_h = pr.y1 - pr.y0;
		}
		for (block = stext->first_block; block != NULL && !oom; block = block->next)
		{
			fz_stext_line *line;
			if (block->type != FZ_STEXT_BLOCK_TEXT)
			{
				continue;
			}
			for (line = block->u.t.first_line; line != NULL && !oom; line = line->next)
			{
				fz_stext_char *ch;
				fz_font *rfont = NULL;
				float rsize = 0.0f;
				fz_rect rb = fz_empty_rect;
				float rox = 0.0f, roy = 0.0f;
				int r_open = 0;

				for (ch = line->first_char; ch != NULL; ch = ch->next)
				{
					if (ch->font == NULL)
					{
						continue;
					}
					if (r_open && (ch->font != rfont || pf_fabsf(ch->size - rsize) > 0.001f))
					{
						char *u = pf_build_run_text(line, rfont, rsize, &oom);
						if (oom)
						{
							break;
						}
						if (n == cap)
						{
							pf_run_s *p;
							int ncap = cap ? cap * 2 : 16;
							p = (pf_run_s *)realloc(runs, (size_t)ncap * sizeof(pf_run_s));
							if (p == NULL)
							{
								free(u);
								oom = 1;
								break;
							}
							runs = p;
							cap = ncap;
						}
						runs[n].x0 = rb.x0;
						runs[n].y0 = PF_FLIPY(page_h, rb.y1);
						runs[n].x1 = rb.x1;
						runs[n].y1 = PF_FLIPY(page_h, rb.y0);
						runs[n].origin_x = rox;
						runs[n].origin_y = PF_FLIPY(page_h, roy);
						runs[n].size = rsize;
						runs[n].font = rfont;
						runs[n].utext = u;
						n++;
						rb = fz_empty_rect;
						r_open = 0;
					}
					if (!r_open)
					{
						rfont = ch->font;
						rsize = ch->size;
						rox = ch->origin.x;
						roy = ch->origin.y;
						rb = fz_empty_rect;
						r_open = 1;
					}
					rb = fz_union_rect(rb, fz_rect_from_quad(ch->quad));
				}
				if (r_open && !oom)
				{
					char *u = pf_build_run_text(line, rfont, rsize, &oom);
					if (!oom)
					{
						int ncap;
						pf_run_s *p;
						if (n == cap)
						{
							ncap = cap ? cap * 2 : 16;
							p = (pf_run_s *)realloc(runs, (size_t)ncap * sizeof(pf_run_s));
							if (p == NULL)
							{
								free(u);
								oom = 1;
							}
							else
							{
								runs = p;
								cap = ncap;
							}
						}
						if (!oom)
						{
							runs[n].x0 = rb.x0;
							runs[n].y0 = PF_FLIPY(page_h, rb.y1);
							runs[n].x1 = rb.x1;
							runs[n].y1 = PF_FLIPY(page_h, rb.y0);
							runs[n].origin_x = rox;
							runs[n].origin_y = PF_FLIPY(page_h, roy);
							runs[n].size = rsize;
							runs[n].font = rfont;
							runs[n].utext = u;
							n++;
						}
					}
				}
			}
		}
		*out = runs;
		*nout = n;
		*out_stext = stext;
		status = oom ? -1 : PF_OK;
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		status = PF_ERR;
	}

	fz_var(status);
	if (page != NULL)
	{
		fz_drop_page(ctx, page);
	}
	if (status != PF_OK)
	{
		pf_free_runs(runs, n);
		if (stext != NULL)
		{
			fz_drop_stext_page(ctx, stext);
		}
		if (status == -1)
		{
			record_error("pf_edit: out of memory building text runs");
		}
	}
	return status;
}

int pf_list_text_runs(pf_context context, pf_document document, int page_index,
                      const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pf_run_s *runs = NULL;
	int nruns = 0;
	fz_stext_page *stext = NULL;
	FILE *fh = NULL;
	int rc, i;

	if (ctx == NULL || doc == NULL || out_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	fh = fopen(out_path_utf8, "wb");
	if (fh == NULL)
	{
		record_error("pf_list_text_runs: cannot open output file");
		return PF_ERR;
	}

	rc = pf_build_runs(ctx, doc, page_index, &runs, &nruns, &stext);
	if (rc != PF_OK)
	{
		fclose(fh);
		return PF_ERR;
	}

	fz_var(stext);
	fz_try(ctx)
	{
		for (i = 0; i < nruns; i++)
		{
			const char *name;
			int embedded;
			const char *c;
			name = fz_font_name(ctx, runs[i].font);
			if (name == NULL)
			{
				name = "";
			}
			embedded = fz_font_ft_face(ctx, runs[i].font) != NULL ? 1 : 0;
			fprintf(fh, "%d\t%g\t%g\t%g\t%g\t%g\t%d\t",
			        i, (double)runs[i].x0, (double)runs[i].y0, (double)runs[i].x1,
			        (double)runs[i].y1, (double)runs[i].size, embedded);
			fprintf(fh, "%s\t", name);
			c = runs[i].utext ? runs[i].utext : "";
			for (; *c != '\0'; ++c)
			{
				unsigned char cc = (unsigned char)*c;
				if (cc == '\t' || cc == '\r' || cc == '\n')
				{
					cc = ' ';
				}
				fputc(cc, fh);
			}
			fputc('\n', fh);
		}
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		fclose(fh);
		fz_drop_stext_page(ctx, stext);
		pf_free_runs(runs, nruns);
		return PF_ERR;
	}

	fclose(fh);
	fz_drop_stext_page(ctx, stext);
	pf_free_runs(runs, nruns);
	return PF_OK;
}

/* ---- content-stream walker ---------------------------------------------- */

typedef struct pf_textw_s
{
	const unsigned char *data;
	size_t len;
	pdf_document *pdf;
	pdf_obj *resources;
	pf_text_op_s *ops;
	int nops, opcap;
	int stream_index;
	int in_text;
	fz_matrix tm, tlm, ctm;
	fz_matrix ctm_stack[PF_MAX_CTM_DEPTH];
	int ctm_depth;
	pf_numring_s ring;
	char namering[PF_MAX_TOKEN_NAME];
	int have_name;
	double tc, tw, tz, tl;
	char font_res[PF_MAX_TOKEN_NAME];
	float font_size;
	int have_font;
	/* FR-EDIT-04 object tracking: the last name, the running numeric byte
	 * offsets, and the most recent cm (its matrix and operand span) so a `Do`
	 * can record the full `cm ... Do` region to splice for move/resize. */
	int have_obj_name;
	char tmp_obj_name[PF_MAX_TOKEN_NAME];
	int num_first_armed;
	size_t num_first_start, num_last_end;
	int pending_cm;
	fz_matrix pending_cm_m;
	size_t pending_cm_start, pending_cm_end;
	size_t pend_start, pend_end;
	pf_dynbuf_s pend;
	int have_pend;
	int in_arr;
	size_t arr_close_end;
	pf_text_op_s *cur;
} pf_textw_s;

static int pf_textw_finalize_string(fz_context *ctx, pf_textw_s *w, int kind,
                                    size_t op_end)
{
	pf_text_op_s *op;
	if (!w->have_pend || w->in_arr)
	{
		w->have_pend = 0;
		return 1;
	}
	if (!pf_opspush(&w->ops, &w->nops, &w->opcap))
	{
		return 0;
	}
	op = &w->ops[w->nops++];
	memset(op, 0, sizeof(*op));
	op->stream_index = w->stream_index;
	op->kind = kind;
	op->span_start = w->pend_start;
	op->span_end = op_end;
	if (w->have_font)
	{
		strcpy(op->font_res, w->font_res);
	}
	op->font_size = w->font_size;
	op->tc = (float)w->tc;
	op->tw = (float)w->tw;
	op->tz = (float)w->tz;
	op->tm = w->tm;
	op->ctm = w->ctm;
	op->bytes = w->pend.data;
	op->nbytes = w->pend.len;
	memset(&w->pend, 0, sizeof(w->pend));
	w->have_pend = 0;
	return 1;
}

static int pf_textw_finalize_array(fz_context *ctx, pf_textw_s *w, size_t end)
{
	pf_text_op_s *op = w->cur;
	if (op == NULL)
	{
		return 1;
	}
	op->stream_index = w->stream_index;
	if (w->have_font && op->font_res[0] == '\0')
	{
		strcpy(op->font_res, w->font_res);
	}
	op->font_size = w->font_size;
	op->tc = (float)w->tc;
	op->tw = (float)w->tw;
	op->tz = (float)w->tz;
	op->tm = w->tm;
	op->ctm = w->ctm;
	op->span_end = end;
	w->cur = NULL;
	w->in_arr = 0;
	w->arr_close_end = 0;
	return 1;
}

/* FR-EDIT-04: record an image/vector invocation (`cm ... Do`) as an object op.
 * Resolves the XObject from the page resources by name so list can report its
 * kind and intrinsic size, and so move/resize knows how to map a target bounds
 * back to a content-stream cm matrix. */
static int pf_objpush(fz_context *ctx, pf_textw_s *w, size_t do_start, size_t do_end)
{
	pdf_obj *namekey = NULL;
	pf_text_op_s *op;
	int tag = PF_OBJ_TAG_IMAGE;
	float ow = 1.0f, oh = 1.0f;

	if (w->have_obj_name && w->tmp_obj_name[0] != '\0' && w->resources != NULL)
	{
		pdf_obj *xobjs = pdf_dict_get(ctx, w->resources, PDF_NAME(XObject));
		pdf_obj *sub = NULL;
		if (xobjs != NULL)
		{
			namekey = pdf_new_name(ctx, w->tmp_obj_name);
			sub = pdf_dict_get(ctx, xobjs, namekey);
			sub = pdf_resolve_indirect(ctx, sub);
			if (sub != NULL)
			{
				if (pdf_name_eq(ctx, pdf_dict_get(ctx, sub, PDF_NAME(Subtype)),
				               PDF_NAME(Form)))
				{
					tag = PF_OBJ_TAG_FORM;
				}
				else
				{
					ow = pdf_to_real(ctx, pdf_dict_get(ctx, sub, PDF_NAME(Width)));
					oh = pdf_to_real(ctx, pdf_dict_get(ctx, sub, PDF_NAME(Height)));
					if (ow <= 0.0f || oh <= 0.0f)
					{
						ow = oh = 1.0f;
					}
				}
			}
			pdf_drop_obj(ctx, namekey);
			namekey = NULL;
		}
	}

	if (!pf_opspush(&w->ops, &w->nops, &w->opcap))
	{
		return 0;
	}
	op = &w->ops[w->nops++];
	memset(op, 0, sizeof(*op));
	op->stream_index = w->stream_index;
	op->kind = PF_OBJ_OP_DO;
	op->obj_ctm = w->pending_cm ? w->pending_cm_m : w->ctm;
	op->obj_has_cm = w->pending_cm;
	if (w->have_obj_name)
	{
		size_t k = strlen(w->tmp_obj_name);
		if (k >= PF_MAX_TOKEN_NAME)
		{
			k = PF_MAX_TOKEN_NAME - 1;
		}
		memcpy(op->obj_name, w->tmp_obj_name, k);
		op->obj_name[k] = '\0';
	}
	op->obj_tag = tag;
	op->obj_w = ow;
	op->obj_h = oh;
	if (w->pending_cm)
	{
		op->span_start = w->pending_cm_start;
		op->span_end = do_end;
	}
	else
	{
		op->span_start = do_start;
		op->span_end = do_end;
	}
	w->pending_cm = 0;
	(void)namekey;
	return 1;
}

static int pf_walk_content(fz_context *ctx, pdf_document *pdf, pdf_obj *resources,
                           const unsigned char *data, size_t len, int stream_index,
                           pf_text_op_s **ops, int *nops, int *opcap)
{
	pf_textw_s w;
	size_t pos = 0;
	unsigned char *sbuf;
	pf_tok_s tok;
	int rc = 1;

	memset(&w, 0, sizeof(w));
	w.data = data;
	w.len = len;
	w.pdf = pdf;
	w.resources = resources;
	w.ops = *ops;
	w.nops = *nops;
	w.opcap = *opcap;
	w.stream_index = stream_index;
	w.tm = fz_identity;
	w.tlm = fz_identity;
	w.ctm = fz_identity;
	w.tz = 100.0;
	w.font_size = 12.0f;

	sbuf = (unsigned char *)malloc(PF_STRING_CAP);
	if (sbuf == NULL)
	{
		return 0;
	}

	while (rc == 1 && pf_next_tok(data, len, &pos, &tok, sbuf))
	{
		switch (tok.kind)
		{
		case 1: /* name */
			if (!w.in_arr)
			{
				size_t k = tok.dlen > PF_MAX_TOKEN_NAME - 1 ? PF_MAX_TOKEN_NAME - 1 : tok.dlen;
				memcpy(w.namering, sbuf, k);
				w.namering[k] = '\0';
				w.have_name = 1;
				w.have_obj_name = 1;
				memcpy(w.tmp_obj_name, w.namering, k + 1);
			}
			break;
		case 2: /* number */
			if (w.in_arr && w.cur != NULL)
			{
				if (!pf_opadj(w.cur, (int)w.cur->nbytes, (float)tok.num))
				{
					rc = 0;
				}
			}
			else
			{
				pf_ring_push(&w.ring, tok.num);
				if (!w.num_first_armed)
				{
					w.num_first_start = tok.start;
					w.num_first_armed = 1;
				}
				w.num_last_end = tok.end;
			}
			break;
		case 3: /* literal */
		case 4: /* hex */
			if (w.in_arr && w.cur != NULL)
			{
				if (!pf_opbytes(w.cur, sbuf, tok.dlen))
				{
					rc = 0;
				}
			}
			else
			{
				w.pend_start = tok.start;
				w.pend_end = tok.end;
				w.have_pend = 1;
				w.pend.len = 0;
				if (tok.dlen > 0 && !pf_dynbuf_push(&w.pend, sbuf, tok.dlen))
				{
					rc = 0;
				}
			}
			break;
		case 5: /* '[' */
			if (!w.in_arr)
			{
				if (!pf_opspush(&w.ops, &w.nops, &w.opcap))
				{
					rc = 0;
					break;
				}
				w.cur = &w.ops[w.nops++];
				memset(w.cur, 0, sizeof(*w.cur));
				w.cur->kind = PF_TEXT_OP_ARR;
				w.cur->span_start = tok.start;
				w.in_arr = 1;
				w.have_pend = 0;
			}
			break;
		case 6: /* ']' */
			if (w.in_arr && w.cur != NULL)
			{
				w.arr_close_end = tok.end;
			}
			break;
		case 7:
		case 8: /* braces ignored */
			break;
		case 10: /* '<<' ignored */
			break;
		case 9: /* operator */
			{
				char opname[16];
				size_t oplen = tok.dlen > 15 ? 15 : tok.dlen;

				memcpy(opname, sbuf, oplen);
				opname[oplen] = '\0';

				if (strcmp(opname, "BT") == 0)
				{
					w.in_text = 1;
					w.tm = fz_identity;
					w.tlm = fz_identity;
				}
				else if (strcmp(opname, "ET") == 0)
				{
					w.in_text = 0;
				}
				else if (strcmp(opname, "q") == 0)
				{
					if (w.ctm_depth < PF_MAX_CTM_DEPTH)
					{
						w.ctm_stack[w.ctm_depth++] = w.ctm;
					}
				}
				else if (strcmp(opname, "Q") == 0)
				{
					if (w.ctm_depth > 0)
					{
						w.ctm = w.ctm_stack[--w.ctm_depth];
					}
				}
				else if (strcmp(opname, "cm") == 0 && w.ring.count >= 6)
				{
					fz_matrix m;
					m.a = (float)pf_ring_at(&w.ring, 0);
					m.b = (float)pf_ring_at(&w.ring, 1);
					m.c = (float)pf_ring_at(&w.ring, 2);
					m.d = (float)pf_ring_at(&w.ring, 3);
					m.e = (float)pf_ring_at(&w.ring, 4);
					m.f = (float)pf_ring_at(&w.ring, 5);
					w.ctm = fz_concat(w.ctm, m);
					w.pending_cm = 1;
					w.pending_cm_m = w.ctm;
					if (w.num_first_armed)
					{
						w.pending_cm_start = w.num_first_start;
						w.pending_cm_end = tok.end;
					}
					else
					{
						w.pending_cm_start = w.pending_cm_end = tok.start;
					}
				}
				else if (strcmp(opname, "Do") == 0 && !w.in_text)
				{
					/* FR-EDIT-04: an image/vector invocation. Record the whole
					 * `cm ... Do` region as the splice span; resolve the XObject
					 * here so list/move know the kind and intrinsic size. */
					if (!pf_objpush(ctx, &w, tok.start, tok.end))
					{
						rc = 0;
					}
				}

				if (w.in_text)
				{
					if (strcmp(opname, "Tf") == 0 && w.have_name && w.ring.count >= 1)
					{
						strcpy(w.font_res, w.namering);
						w.font_size = (float)pf_ring_at(&w.ring, 0);
						w.have_font = 1;
					}
					else if (strcmp(opname, "Tc") == 0 && w.ring.count >= 1)
					{
						w.tc = pf_ring_at(&w.ring, 0);
					}
					else if (strcmp(opname, "Tw") == 0 && w.ring.count >= 1)
					{
						w.tw = pf_ring_at(&w.ring, 0);
					}
					else if (strcmp(opname, "Tz") == 0 && w.ring.count >= 1)
					{
						w.tz = pf_ring_at(&w.ring, 0);
					}
					else if (strcmp(opname, "TL") == 0 && w.ring.count >= 1)
					{
						w.tl = pf_ring_at(&w.ring, 0);
					}
					else if (strcmp(opname, "Td") == 0 && w.ring.count >= 2)
					{
						fz_matrix m = fz_translate((float)pf_ring_at(&w.ring, 0),
						                           (float)pf_ring_at(&w.ring, 1));
						w.tlm = fz_concat(m, w.tlm);
						w.tm = w.tlm;
					}
					else if (strcmp(opname, "TD") == 0 && w.ring.count >= 2)
					{
						fz_matrix m = fz_translate((float)pf_ring_at(&w.ring, 0),
						                           -(float)pf_ring_at(&w.ring, 1));
						w.tlm = fz_concat(m, w.tlm);
						w.tm = w.tlm;
					}
					else if (strcmp(opname, "Tm") == 0 && w.ring.count >= 6)
					{
						fz_matrix m;
						m.a = (float)pf_ring_at(&w.ring, 0);
						m.b = (float)pf_ring_at(&w.ring, 1);
						m.c = (float)pf_ring_at(&w.ring, 2);
						m.d = (float)pf_ring_at(&w.ring, 3);
						m.e = (float)pf_ring_at(&w.ring, 4);
						m.f = (float)pf_ring_at(&w.ring, 5);
						w.tm = m;
						w.tlm = m;
					}
					else if (strcmp(opname, "T*") == 0)
					{
						fz_matrix m = fz_translate(0.0f, (float)-w.tl);
						w.tlm = fz_concat(m, w.tlm);
						w.tm = w.tlm;
					}
					else if (strcmp(opname, "Tj") == 0 || strcmp(opname, "'") == 0)
					{
						int kind = (opname[0] == '\'') ? PF_TEXT_OP_APOST : PF_TEXT_OP_TJ;
						if (!pf_textw_finalize_string(ctx, &w, kind, tok.end))
						{
							rc = 0;
						}
					}
					else if (strcmp(opname, "TJ") == 0)
					{
						size_t end = w.arr_close_end ? w.arr_close_end : tok.end;
						if (!pf_textw_finalize_array(ctx, &w, end))
						{
							rc = 0;
						}
					}
					else if (strcmp(opname, "\"") == 0)
					{
						/* the "" operator is unsupported in 2B; discard its operand */
						w.have_pend = 0;
						w.pend.len = 0;
					}
					else
					{
						w.have_pend = 0;
						w.pend.len = 0;
					}
				}

				pf_ring_clear(&w.ring);
				w.num_first_armed = 0;
				w.have_name = 0;
				w.have_obj_name = 0;
			}
			break;
		default:
			break;
		}
	}

	if (w.in_arr && w.cur != NULL)
	{
		size_t end = w.arr_close_end ? w.arr_close_end : w.cur->span_start + 1;
		pf_textw_finalize_array(ctx, &w, end);
	}
	pf_dynbuf_free(&w.pend);
	free(sbuf);
	*ops = w.ops;
	*nops = w.nops;
	*opcap = w.opcap;
	return rc;
}

static int pf_page_stream_count(fz_context *ctx, pdf_document *pdf, int page_index,
                                int *nstreams)
{
	pdf_obj *pageobj, *contents;
	pageobj = pdf_lookup_page_obj(ctx, pdf, page_index);
	contents = pdf_dict_get_inheritable(ctx, pageobj, PDF_NAME(Contents));
	if (contents == NULL)
	{
		record_error("pf_edit: page has no content stream");
		return 0;
	}
	if (pdf_is_array(ctx, contents))
	{
		*nstreams = pdf_array_len(ctx, contents);
	}
	else
	{
		*nstreams = 1;
	}
	return 1;
}

static pdf_obj *pf_page_stream_obj(fz_context *ctx, pdf_document *pdf,
                                   int page_index, int stream_index, int *nstreams)
{
	pdf_obj *pageobj, *contents;
	pageobj = pdf_lookup_page_obj(ctx, pdf, page_index);
	contents = pdf_dict_get_inheritable(ctx, pageobj, PDF_NAME(Contents));
	if (contents == NULL)
	{
		record_error("pf_edit: page has no content stream");
		return NULL;
	}
	if (pdf_is_array(ctx, contents))
	{
		int n = pdf_array_len(ctx, contents);
		*nstreams = n;
		if (stream_index < 0 || stream_index >= n)
		{
			record_error("pf_edit: content stream index out of range");
			return NULL;
		}
		return pdf_array_get(ctx, contents, stream_index);
	}
	*nstreams = 1;
	if (stream_index != 0)
	{
		record_error("pf_edit: content stream index out of range");
		return NULL;
	}
	return contents;
}

/* Replaces [offset, offset+removelen) of content stream stream_index on the
 * page with `insert`. Updates the stream in memory; pf_save_document persists.
 * Content streams are loaded as decoded buffers, so the offsets and byte
 * lengths the walker/operator store are in decoded coordinates. */
static int pf_splice_stream(fz_context *ctx, pdf_document *pdf, int page_index,
                            int stream_index, size_t offset, size_t removelen,
                            const unsigned char *insert, size_t insertlen)
{
	pdf_obj *obj;
	fz_buffer *buf, *nb;
	unsigned char *data = NULL;
	size_t len;
	int nstreams;

	obj = pf_page_stream_obj(ctx, pdf, page_index, stream_index, &nstreams);
	if (obj == NULL)
	{
		return PF_ERR;
	}
	buf = pdf_load_stream(ctx, obj);
	len = fz_buffer_storage(ctx, buf, &data);
	if (data == NULL || offset > len || offset + removelen > len)
	{
		record_error("pf_edit: splice range outside the content stream");
		fz_drop_buffer(ctx, buf);
		return PF_ERR;
	}
	nb = fz_new_buffer(ctx, len - removelen + insertlen);
	fz_append_data(ctx, nb, data, offset);
	fz_append_data(ctx, nb, insert, insertlen);
	fz_append_data(ctx, nb, data + offset + removelen, len - offset - removelen);
	pdf_update_stream(ctx, pdf, obj, nb, 0);
	fz_drop_buffer(ctx, nb);
	fz_drop_buffer(ctx, buf);
	return PF_OK;
}

static int pf_escape_literal(const unsigned char *in, size_t n, pf_dynbuf_s *out)
{
	size_t i;
	for (i = 0; i < n; i++)
	{
		unsigned char c = in[i];
		unsigned char tmp[8];
		if (c == '\n')
		{
			tmp[0] = '\\';
			tmp[1] = 'n';
			if (!pf_dynbuf_push(out, tmp, 2))
			{
				return 0;
			}
		}
		else if (c == '\r')
		{
			tmp[0] = '\\';
			tmp[1] = 'r';
			if (!pf_dynbuf_push(out, tmp, 2))
			{
				return 0;
			}
		}
		else if (c == '\t')
		{
			tmp[0] = '\\';
			tmp[1] = 't';
			if (!pf_dynbuf_push(out, tmp, 2))
			{
				return 0;
			}
		}
		else if (c == '\b' || c == '\f')
		{
			tmp[0] = '\\';
			tmp[1] = (c == '\b') ? 'b' : 'f';
			if (!pf_dynbuf_push(out, tmp, 2))
			{
				return 0;
			}
		}
		else if (c == '(' || c == ')' || c == '\\')
		{
			tmp[0] = '\\';
			tmp[1] = c;
			if (!pf_dynbuf_push(out, tmp, 2))
			{
				return 0;
			}
		}
		else if (c < 0x20 || c >= 0x7F)
		{
			unsigned char oct[4];
			PF_SNPRINTF((char *)oct, sizeof(oct), _TRUNCATE, "\\%03o", (unsigned)c);
			if (!pf_dynbuf_push(out, oct, 4))
			{
				return 0;
			}
		}
		else
		{
			if (!pf_dynbuf_pushc(out, c))
			{
				return 0;
			}
		}
	}
	return 1;
}

/* Encodes UTF-8 new text into the run font's PDF doc encoding. CR/LF collapse
 * to space (a content literal cannot carry a line feed for a Tj). Failure
 * records the unencodable character and returns 0. */
static int pf_encode_new_text(fz_context *ctx, fz_font *font, const char *utf8,
                              const unsigned short *docenc, pf_dynbuf_s *out)
{
	const char *p = utf8;
	while (*p != '\0')
	{
		int rune, cl;
		int found = 0;
		unsigned char byte = 0;
		cl = fz_chartorune(&rune, p);
		if (cl <= 0 || rune < 0)
		{
			record_error("pf_edit: invalid UTF-8 in new text");
			return 0;
		}
		if (rune == 0x0A || rune == 0x0D)
		{
			rune = (int)0x20;
		}
		if (rune == 0x20)
		{
			byte = 0x20;
			found = 1;
		}
		else
		{
			int b;
			for (b = 0; b < 256; b++)
			{
				if (docenc[b] == (unsigned short)rune)
				{
					byte = (unsigned char)b;
					found = 1;
					break;
				}
			}
			if (found && font != NULL)
			{
				unsigned short ucs = docenc[byte];
				int gid = fz_encode_character(ctx, font, ucs);
				if (gid <= 0)
				{
char msg[192];
				PF_SNPRINTF(msg, sizeof(msg), _TRUNCATE,
				            "pf_edit: character U+%04X has no glyph in the run's font",
				            rune);
					record_error(msg);
					return 0;
				}
			}
		}
		if (!found)
		{
			char msg[192];
			PF_SNPRINTF(msg, sizeof(msg), _TRUNCATE,
			            "pf_edit: character U+%04X cannot be encoded by the run's font",
			            rune);
			record_error(msg);
			return 0;
		}
		if (!pf_dynbuf_pushc(out, byte))
		{
			return 0;
		}
		p += cl;
	}
	return 1;
}

static unsigned char *pf_read_file(const char *path, size_t *outlen)
{
	FILE *fh;
	unsigned char *buf;
	long sz;
	if (outlen != NULL)
	{
		*outlen = 0;
	}
	fh = fopen(path, "rb");
	if (fh == NULL)
	{
		return NULL;
	}
	if (fseek(fh, 0, SEEK_END) != 0)
	{
		fclose(fh);
		return NULL;
	}
	sz = ftell(fh);
	if (sz < 0)
	{
		fclose(fh);
		return NULL;
	}
	if (fseek(fh, 0, SEEK_SET) != 0)
	{
		fclose(fh);
		return NULL;
	}
	buf = (unsigned char *)malloc((size_t)sz + 1);
	if (buf == NULL)
	{
		fclose(fh);
		return NULL;
	}
	if (sz > 0 && fread(buf, 1, (size_t)sz, fh) != (size_t)sz)
	{
		free(buf);
		fclose(fh);
		return NULL;
	}
	fclose(fh);
	buf[sz] = '\0';
	if (outlen != NULL)
	{
		*outlen = (size_t)sz;
	}
	return buf;
}

static const char pf_b64_tab[] =
	"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

static void pf_b64_encode(const unsigned char *in, size_t n, char *out)
{
	size_t i, j = 0;
	for (i = 0; i + 2 < n; i += 3)
	{
		unsigned v = (in[i] << 16) | (in[i + 1] << 8) | in[i + 2];
		out[j++] = pf_b64_tab[(v >> 18) & 0x3F];
		out[j++] = pf_b64_tab[(v >> 12) & 0x3F];
		out[j++] = pf_b64_tab[(v >> 6) & 0x3F];
		out[j++] = pf_b64_tab[v & 0x3F];
	}
	if (n - i == 1)
	{
		unsigned v = in[i] << 16;
		out[j++] = pf_b64_tab[(v >> 18) & 0x3F];
		out[j++] = pf_b64_tab[(v >> 12) & 0x3F];
		out[j++] = '=';
		out[j++] = '=';
	}
	else if (n - i == 2)
	{
		unsigned v = (in[i] << 16) | (in[i + 1] << 8);
		out[j++] = pf_b64_tab[(v >> 18) & 0x3F];
		out[j++] = pf_b64_tab[(v >> 12) & 0x3F];
		out[j++] = pf_b64_tab[(v >> 6) & 0x3F];
		out[j++] = '=';
	}
	out[j] = '\0';
}

static int pf_b64val(char c)
{
	if (c >= 'A' && c <= 'Z')
	{
		return c - 'A';
	}
	if (c >= 'a' && c <= 'z')
	{
		return c - 'a' + 26;
	}
	if (c >= '0' && c <= '9')
	{
		return c - '0' + 52;
	}
	if (c == '+')
	{
		return 62;
	}
	if (c == '/')
	{
		return 63;
	}
	return -1;
}

static int pf_b64_decode(const char *in, size_t n, unsigned char **outp, size_t *outn)
{
	size_t i = 0, j = 0, cap = n / 4 * 3 + 3;
	unsigned char *out = (unsigned char *)malloc(cap);
	if (out == NULL)
	{
		return 0;
	}
	while (i + 4 <= n)
	{
		int v0 = pf_b64val(in[i]);
		int v1 = pf_b64val(in[i + 1]);
		int v2 = in[i + 2] == '=' ? -1 : pf_b64val(in[i + 2]);
		int v3 = in[i + 3] == '=' ? -1 : pf_b64val(in[i + 3]);
		if (v0 < 0 || v1 < 0 || j + 3 > cap)
		{
			free(out);
			return 0;
		}
		out[j++] = (unsigned char)((v0 << 2) | (v1 >> 4));
		if (v2 >= 0)
		{
			out[j++] = (unsigned char)(((v1 & 0x0F) << 4) | (v2 >> 2));
		}
		if (v3 >= 0)
		{
			out[j++] = (unsigned char)(((v2 & 0x03) << 6) | v3);
		}
		i += 4;
	}
	*outp = out;
	*outn = j;
	return 1;
}

static int pf_parse_receipt(const unsigned char *buf, size_t len,
                            int *stream_index, size_t *offset, size_t *oldlen,
                            size_t *newlen, unsigned char **oldb, size_t *oldblen,
                            unsigned char **newb, size_t *newblen)
{
	size_t p = 0;
	int got_r = 0, got_o = 0, got_n = 0;
	while (p < len)
	{
		size_t nl;
		size_t ll;
		char line[512];
		const char *nlp = (const char *)memchr(buf + p, '\n', len - p);
		nl = nlp ? (size_t)(nlp - (const char *)(buf + p)) : (len - p);
		ll = nl;
		if (ll > sizeof(line) - 1)
		{
			ll = sizeof(line) - 1;
		}
		memcpy(line, buf + p, ll);
		line[ll] = '\0';
		while (ll > 0 && (line[ll - 1] == '\r' || line[ll - 1] == '\n'))
		{
			line[--ll] = '\0';
		}
		if (strncmp(line, "PF-TRW", 6) == 0 || ll == 0)
		{
			/* header / blank line, nothing to capture */
		}
		else if (line[0] == 'R' && line[1] == '\t')
		{
			int s = 0;
			unsigned long long o1 = 0, o2 = 0, o3 = 0;
			if (sscanf(line, "R\t%d\t%llu\t%llu\t%llu", &s, &o1, &o2, &o3) == 4)
			{
				*stream_index = s;
				*offset = (size_t)o1;
				*oldlen = (size_t)o2;
				*newlen = (size_t)o3;
				got_r = 1;
			}
		}
		else if (line[0] == 'O' && line[1] == '\t')
		{
			if (!pf_b64_decode(line + 2, strlen(line + 2), oldb, oldblen))
			{
				return 0;
			}
			got_o = 1;
		}
		else if (line[0] == 'N' && line[1] == '\t')
		{
			if (!pf_b64_decode(line + 2, strlen(line + 2), newb, newblen))
			{
				return 0;
			}
			got_n = 1;
		}
		p += nl + 1;
		if (nlp == NULL)
		{
			break;
		}
	}
	return got_r && got_o && got_n;
}

static int pf_find_run_op(pf_text_op_s *ops, int nops, pf_run_s *target,
                          int *out_index)
{
	int i, best_eq = -1, best_any = -1;
	double best_eq_d = 1e30, best_any_d = 1e30;
	double tx = target->origin_x, ty = target->origin_y;
	for (i = 0; i < nops; i++)
	{
		double dx, dy, d;
		if (!ops[i].geom_ok)
		{
			continue;
		}
		dx = ops[i].origin_x - tx;
		dy = ops[i].origin_y - ty;
		d = dx * dx + dy * dy;
		if (d > PF_ORIGIN_TOLERANCE * PF_ORIGIN_TOLERANCE)
		{
			continue;
		}
		if (d < best_any_d)
		{
			best_any_d = d;
			best_any = i;
		}
		if (ops[i].utext != NULL && target->utext != NULL &&
		    strcmp(ops[i].utext, target->utext) == 0)
		{
			if (d < best_eq_d)
			{
				best_eq_d = d;
				best_eq = i;
			}
		}
	}
	*out_index = best_eq >= 0 ? best_eq : best_any;
	return best_eq >= 0 ? 1 : (best_any >= 0 ? 0 : -1);
}

int pf_rewrite_text_run(pf_context context, pf_document document, int page_index,
                        int run_index, const char *new_text_path_utf8,
                        const char *receipt_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	pf_run_s *runs = NULL;
	int nruns = 0;
	fz_stext_page *stext = NULL;
	unsigned char *ntext = NULL;
	pf_text_op_s *ops = NULL;
	int nops = 0, opcap = 0;
	pf_fontcache_s fc;
	pdf_obj *pageobj = NULL;
	pdf_obj *resources = NULL;
	int nstreams = 0;
	int i, match_rc, mi = -1;
	pf_text_op_s *op;
	pdf_font_desc *fdesc = NULL;
	fz_font *font;
	unsigned short docenc[256];
	pf_dynbuf_s enc = { NULL, 0, 0 };
	pf_dynbuf_s newop = { NULL, 0, 0 };
	unsigned char *oldbytes = NULL;
	size_t oldlen = 0, offset = 0;
	pf_text_op_s nop;
	fz_rect newbbox;
	fz_rect oldbbox;
	FILE *fh = NULL;
	char *b64old = NULL, *b64new = NULL;
	int status = PF_ERR;
	int j;

	if (ctx == NULL || doc == NULL || new_text_path_utf8 == NULL ||
	    receipt_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	memset(&fc, 0, sizeof(fc));
	memset(&nop, 0, sizeof(nop));
	fz_var(pdf);
	fz_var(ops);
	fz_var(runs);
	fz_var(stext);
	fz_var(ntext);
	fz_var(oldbytes);
	fz_var(b64old);
	fz_var(b64new);

	for (j = 0; j < 256; j++)
	{
		unsigned short ucs = fz_unicode_from_pdf_doc_encoding[j];
		docenc[j] = (ucs == 0) ? (unsigned short)(j < 0x80 ? j : '?') : ucs;
	}

	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: not a PDF document");
		}

		{
			int rcr = pf_build_runs(ctx, doc, page_index, &runs, &nruns, &stext);
			if (rcr != PF_OK)
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: could not build text runs");
			}
		}
		if (run_index < 0 || run_index >= nruns)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: run index out of range");
		}
		ntext = pf_read_file(new_text_path_utf8, NULL);
		if (ntext == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: cannot read new text file");
		}
		{
			size_t sl = strlen((const char *)ntext);
			while (sl > 0 && (ntext[sl - 1] == '\n' || ntext[sl - 1] == '\r'))
			{
				ntext[--sl] = '\0';
			}
		}

		if (!pf_page_stream_count(ctx, pdf, page_index, &nstreams))
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: page has no content stream");
		}
		pageobj = pdf_lookup_page_obj(ctx, pdf, page_index);
		resources = pdf_dict_get_inheritable(ctx, pageobj, PDF_NAME(Resources));
		if (resources == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: page has no resources");
		}

		for (i = 0; i < nstreams; i++)
		{
			pdf_obj *obj = pf_page_stream_obj(ctx, pdf, page_index, i, &nstreams);
			fz_buffer *buf;
			unsigned char *data;
			size_t len;
			if (obj == NULL)
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: content stream missing");
			}
			buf = pdf_load_stream(ctx, obj);
			len = fz_buffer_storage(ctx, buf, &data);
			if (!pf_walk_content(ctx, pdf, resources, data, len, i,
			                     &ops, &nops, &opcap))
			{
				fz_drop_buffer(ctx, buf);
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: out of memory walking content");
			}
			fz_drop_buffer(ctx, buf);
		}

		for (i = 0; i < nops; i++)
		{
			pf_op_geometry(ctx, pdf, resources, &ops[i], &fc);
		}

		match_rc = pf_find_run_op(ops, nops, &runs[run_index], &mi);
		if (match_rc < 0)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC,
			         "pf_rewrite_text_run: no content operator paints the run at its origin");
		}
		if (match_rc == 0)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC,
			         "pf_rewrite_text_run: matching operator found but its decoded text differs from the run; run not rewritten");
		}
		op = &ops[mi];

		fdesc = pf_resolve_font(ctx, pdf, resources, op->font_res, &fc);
		font = fdesc ? fdesc->font : NULL;
		if (!pf_encode_new_text(ctx, font, (const char *)ntext, docenc, &enc))
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: new text is not encodable by the run's font");
		}

		/* Geometry of the new run: same operator state, new glyph bytes. */
		memset(&nop, 0, sizeof(nop));
		nop.stream_index = op->stream_index;
		nop.kind = op->kind;
		if (op->font_res[0])
		{
			strcpy(nop.font_res, op->font_res);
		}
		nop.font_size = op->font_size;
		nop.tc = op->tc;
		nop.tw = op->tw;
		nop.tz = op->tz;
		nop.tm = op->tm;
		nop.ctm = op->ctm;
		nop.bytes = enc.data;
		nop.nbytes = enc.len;
		pf_op_geometry(ctx, pdf, resources, &nop, &fc);
		oldbbox = op->bbox;
		newbbox = nop.bbox;
		free(nop.utext);

		/* Build the replacement operator bytes. */
		if (!pf_dynbuf_pushc(&newop, '('))
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: out of memory building the replacement");
		}
		if (op->kind == PF_TEXT_OP_APOST)
		{
			if (!pf_dynbuf_push(&newop, (const unsigned char *)"T* ", 3))
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: out of memory building the replacement");
			}
		}
		if (!pf_escape_literal(enc.data, enc.len, &newop))
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: out of memory building the replacement");
		}
		if (!pf_dynbuf_push(&newop, (const unsigned char *)") Tj", 4))
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: out of memory building the replacement");
		}

		/* Rewrite the operator bytes in the op's stream, then capture the
		 * old operator bytes (raw up to the operator keyword). */
		offset = op->span_start;
		oldlen = op->span_end - op->span_start;
		if (oldlen == 0)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: operator span is empty");
		}
		oldbytes = (unsigned char *)malloc(oldlen);
		if (oldbytes == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: out of memory");
		}
		{
			pdf_obj *obj = pf_page_stream_obj(ctx, pdf, page_index, op->stream_index, &nstreams);
			fz_buffer *buf;
			unsigned char *data;
			size_t len;
			if (obj == NULL)
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: content stream missing");
			}
			buf = pdf_load_stream(ctx, obj);
			len = fz_buffer_storage(ctx, buf, &data);
			if (data == NULL || offset > len || offset + oldlen > len)
			{
				fz_drop_buffer(ctx, buf);
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: operator span outside the content stream");
			}
			memcpy(oldbytes, data + offset, oldlen);
			fz_drop_buffer(ctx, buf);
		}

		if (pf_splice_stream(ctx, pdf, page_index, op->stream_index, offset,
		                     oldlen, newop.data, newop.len) != PF_OK)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: content stream splice failed");
		}

		/* Receipt: stream/offset/lengths plus the old and new operator
		 * bytes, base64, for FR-EDIT-05 undo/redo. */
		fh = fopen(receipt_path_utf8, "wb");
		if (fh == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: cannot open receipt file");
		}
		b64old = (char *)calloc(oldlen / 3 * 4 + 8, 1);
		b64new = (char *)calloc(newop.len / 3 * 4 + 8, 1);
		if (b64old == NULL || b64new == NULL)
		{
			fclose(fh);
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_rewrite_text_run: out of memory writing the receipt");
		}
		pf_b64_encode(oldbytes, oldlen, b64old);
		pf_b64_encode(newop.data, newop.len, b64new);
		fprintf(fh, "PF-TRW\t1\n");
		fprintf(fh, "R\t%d\t%llu\t%llu\t%llu\n", op->stream_index,
		        (unsigned long long)offset, (unsigned long long)oldlen,
		        (unsigned long long)newop.len);
		fprintf(fh, "O\t%s\n", b64old);
		fprintf(fh, "N\t%s\n", b64new);
		fclose(fh);
		fh = NULL;

		status = PF_OK;
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		status = PF_ERR;
	}

	pf_fontcache_free(ctx, &fc);
	pf_free_text_ops(ops, nops);
	pf_free_runs(runs, nruns);
	if (stext != NULL)
	{
		fz_drop_stext_page(ctx, stext);
	}
	free(ntext);
	free(oldbytes);
	free(b64old);
	free(b64new);
	pf_dynbuf_free(&enc);
	pf_dynbuf_free(&newop);
	if (fh != NULL)
	{
		fclose(fh);
	}
	(void)oldbbox;
	(void)newbbox;
	return status;
}

int pf_revert_text_rewrite(pf_context context, pf_document document, int page_index,
                           const char *receipt_path_utf8, int redo_flag)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	unsigned char *rbuf = NULL;
	size_t rlen = 0;
	int stream_index = 0;
	size_t offset = 0, oldlen = 0, newlen = 0;
	unsigned char *oldb = NULL, *newb = NULL;
	size_t oldblen = 0, newblen = 0;
	int status = PF_ERR;

	if (ctx == NULL || doc == NULL || receipt_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	fz_var(pdf);
	fz_var(rbuf);
	fz_var(oldb);
	fz_var(newb);

	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_revert_text_rewrite: not a PDF document");
		}
		rbuf = pf_read_file(receipt_path_utf8, &rlen);
		if (rbuf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_revert_text_rewrite: cannot read the receipt file");
		}
		if (!pf_parse_receipt(rbuf, rlen, &stream_index, &offset, &oldlen,
		                      &newlen, &oldb, &oldblen, &newb, &newblen))
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_revert_text_rewrite: malformed receipt");
		}
		if (oldblen == 0 || newblen == 0)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_revert_text_rewrite: receipt has empty operator bytes");
		}
		if (redo_flag)
		{
			if (pf_splice_stream(ctx, pdf, page_index, stream_index, offset,
			                     oldlen, newb, newblen) != PF_OK)
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_revert_text_rewrite: redo splice failed");
			}
		}
		else
		{
			if (pf_splice_stream(ctx, pdf, page_index, stream_index, offset,
			                     newlen, oldb, oldblen) != PF_OK)
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_revert_text_rewrite: undo splice failed");
			}
		}
		status = PF_OK;
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
		status = PF_ERR;
	}

	free(rbuf);
	free(oldb);
	free(newb);
	return status;
}


/* FR-EDIT-04: device-space bbox of an image/vector object. The object's cm maps
 * its intrinsic rect (0,0,ow,oh) into device space; report the bounding box. */
static void pf_obj_bbox(const pf_text_op_s *op, float *x0, float *y0,
                        float *x1, float *y1)
{
	fz_point p0 = fz_transform_point(fz_make_point(0, 0), op->obj_ctm);
	fz_point p1 = fz_transform_point(fz_make_point(op->obj_w, op->obj_h),
	                                 op->obj_ctm);
	*x0 = p0.x; *y0 = p0.y; *x1 = p1.x; *y1 = p1.y;
	if (*x0 > *x1) { float t = *x0; *x0 = *x1; *x1 = t; }
	if (*y0 > *y1) { float t = *y0; *y0 = *y1; *y1 = t; }
}

/* Collect the content-stream walk's tracks across every page stream. Returns
 * the ops array (caller frees with pf_free_text_ops). */
static int pf_collect_ops(fz_context *ctx, pdf_document *pdf, int page_index,
                          pdf_obj **out_resources, int *out_nstreams,
                          pf_text_op_s **ops, int *nops, int *opcap)
{
	pdf_obj *pageobj;
	pdf_obj *resources;
	int nstreams = 0;
	int i;

	if (!pf_page_stream_count(ctx, pdf, page_index, &nstreams))
	{
		return PF_ERR;
	}
	pageobj = pdf_lookup_page_obj(ctx, pdf, page_index);
	resources = pdf_dict_get_inheritable(ctx, pageobj, PDF_NAME(Resources));
	if (resources == NULL)
	{
		record_error("pf_edit: page has no resources");
		return PF_ERR;
	}

	for (i = 0; i < nstreams; i++)
	{
		pdf_obj *obj = pf_page_stream_obj(ctx, pdf, page_index, i, &nstreams);
		fz_buffer *buf;
		unsigned char *data;
		size_t len;
		if (obj == NULL)
		{
			return PF_ERR;
		}
		buf = pdf_load_stream(ctx, obj);
		len = fz_buffer_storage(ctx, buf, &data);
		if (!pf_walk_content(ctx, pdf, resources, data, len, i,
		                     ops, nops, opcap))
		{
			fz_drop_buffer(ctx, buf);
			return PF_ERR;
		}
		fz_drop_buffer(ctx, buf);
	}

	*out_resources = resources;
	*out_nstreams = nstreams;
	return PF_OK;
}

int pf_list_objects(pf_context context, pf_document document, int page_index,
                    const char *out_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	pf_text_op_s *ops = NULL;
	int nops = 0, opcap = 0;
	pdf_obj *resources = NULL;
	int nstreams = 0;
	FILE *fh = NULL;
	int i, j = 0;
	int status = PF_ERR;

	if (ctx == NULL || doc == NULL || out_path_utf8 == NULL)
	{
		return PF_ERR;
	}

	fh = fopen(out_path_utf8, "wb");
	if (fh == NULL)
	{
		record_error("pf_list_objects: cannot open output file");
		return PF_ERR;
	}

	fz_var(pdf);
	fz_var(ops);
	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_list_objects: not a PDF document");
		}
		if (pf_collect_ops(ctx, pdf, page_index, &resources, &nstreams,
		                   &ops, &nops, &opcap) != PF_OK)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_list_objects: could not walk the page content");
		}

		for (i = 0; i < nops; i++)
		{
			pf_text_op_s *op = &ops[i];
			float x0, y0, x1, y1;
			if (op->kind != PF_OBJ_OP_DO)
			{
				continue;
			}
			pf_obj_bbox(op, &x0, &y0, &x1, &y1);
			fprintf(fh, "%d\t%d\t%s\t%g\t%g\t%g\t%g\t%d\t%zu\t%zu\n",
			        j, op->obj_tag, op->obj_name,
			        (double)x0, (double)y0, (double)x1, (double)y1,
			        op->stream_index, op->span_start, op->span_end);
			j++;
		}
		status = PF_OK;
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
	}

	fclose(fh);
	pf_free_text_ops(ops, nops);
	(void)resources;
	(void)nstreams;
	return status;
}

int pf_move_resize_object(pf_context context, pf_document document,
                          int page_index, int object_index,
                          double x0, double y0, double x1, double y1,
                          const char *receipt_path_utf8)
{
	fz_context *ctx = (fz_context *)context;
	fz_document *doc = (fz_document *)document;
	pdf_document *pdf = NULL;
	pf_text_op_s *ops = NULL;
	int nops = 0, opcap = 0;
	pf_text_op_s *op = NULL;
	int nstreams = 0;
	FILE *fh = NULL;
	int i, j = 0;
	unsigned char *oldbytes = NULL;
	size_t oldlen = 0, offset = 0;
	char newop[768];
	size_t newlen;
	char *b64old = NULL, *b64new = NULL;
	char *name = NULL;
	double na, nb, nc, nd, ne, nf;
	int status = PF_ERR;

	if (ctx == NULL || doc == NULL || receipt_path_utf8 == NULL )
	{
		return PF_ERR;
	}

	fz_var(pdf);
	fz_var(ops);
	fz_try(ctx)
	{
		pdf = as_pdf_document(ctx, doc);
		if (pdf == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: not a PDF document");
		}
		{
			pdf_obj *resources = NULL;
			if (pf_collect_ops(ctx, pdf, page_index, &resources, &nstreams,
			                   &ops, &nops, &opcap) != PF_OK)
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: could not walk the page content");
			}
		}

		for (i = 0; i < nops; i++)
		{
			pf_text_op_s *o = &ops[i];
			if (o->kind != PF_OBJ_OP_DO)
			{
				continue;
			}
			if (j == object_index)
			{
				op = o;
				break;
			}
			j++;
		}
		if (op == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: object index out of range");
		}

		/* Map the target bounds back to a content-stream cm matrix. The object's
		 * intrinsic rect (0,0,ow,oh) must land on (x0,y0,x1,y1). */
		na = (x1 - x0) / (double)op->obj_w;
		nd = (y1 - y0) / (double)op->obj_h;
		nb = 0.0;
		nc = 0.0;
		ne = x0;
		nf = y0;
		name = op->obj_name;
		if (name == NULL || name[0] == '\0')
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: object has no resource name");
		}
		newlen = (size_t)PF_SNPRINTF(newop, sizeof(newop), _TRUNCATE,
		                             "%g %g %g %g %g %g /%s Do",
		                             na, nb, nc, nd, ne, nf, name);
		if (newlen == (size_t)-1)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: replacement is too long");
		}

		offset = op->span_start;
		oldlen = op->span_end - op->span_start;
		if (oldlen == 0)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: object span is empty");
		}
		oldbytes = (unsigned char *)malloc(oldlen);
		if (oldbytes == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: out of memory");
		}
		{
			pdf_obj *obj = pf_page_stream_obj(ctx, pdf, page_index,
			                                  op->stream_index, &nstreams);
			fz_buffer *buf;
			unsigned char *data;
			size_t len;
			if (obj == NULL)
			{
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: content stream missing");
			}
			buf = pdf_load_stream(ctx, obj);
			len = fz_buffer_storage(ctx, buf, &data);
			if (data == NULL || offset > len || offset + oldlen > len)
			{
				fz_drop_buffer(ctx, buf);
				fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: object span outside the content stream");
			}
			memcpy(oldbytes, data + offset, oldlen);
			fz_drop_buffer(ctx, buf);
		}

		if (pf_splice_stream(ctx, pdf, page_index, op->stream_index, offset,
		                     oldlen, (const unsigned char *)newop, newlen) != PF_OK)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: content stream splice failed");
		}

		fh = fopen(receipt_path_utf8, "wb");
		if (fh == NULL)
		{
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: cannot open receipt file");
		}
		b64old = (char *)calloc(oldlen / 3 * 4 + 8, 1);
		b64new = (char *)calloc(newlen / 3 * 4 + 8, 1);
		if (b64old == NULL || b64new == NULL)
		{
			fclose(fh);
			fh = NULL;
			fz_throw(ctx, FZ_ERROR_GENERIC, "pf_move_resize_object: out of memory writing the receipt");
		}
		pf_b64_encode(oldbytes, oldlen, b64old);
		pf_b64_encode((const unsigned char *)newop, newlen, b64new);
		fprintf(fh, "PF-TRW\t1\n");
		fprintf(fh, "R\t%d\t%llu\t%llu\t%llu\n", op->stream_index,
		        (unsigned long long)offset, (unsigned long long)oldlen,
		        (unsigned long long)newlen);
		fprintf(fh, "O\t%s\n", b64old);
		fprintf(fh, "N\t%s\n", b64new);
		fclose(fh);
		fh = NULL;

		status = PF_OK;
	}
	fz_catch(ctx)
	{
		caught_message(ctx);
	}

	pf_free_text_ops(ops, nops);
	free(oldbytes);
	free(b64old);
	free(b64new);
	if (fh != NULL)
	{
		fclose(fh);
	}
	return status;
}
