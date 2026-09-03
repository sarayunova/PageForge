// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.AspNetCore.Mvc;

namespace PageForge.Api.Controllers;

[ApiController]
[Route("api/v1/source")]
public sealed class SourceController : ControllerBase
{
    [HttpGet]
    public IActionResult GetSourceInfo()
    {
        string? commitSha = Environment.GetEnvironmentVariable("PAGEFORGE_COMMIT_SHA") ?? "unknown";
        string? repoUrl = Environment.GetEnvironmentVariable("PAGEFORGE_REPO_URL")
            ?? "https://github.com/sarayunova/PageForge";

        return Ok(new
        {
            license = "AGPL-3.0-only",
            repository = repoUrl,
            commit = commitSha,
            notice = "This software is licensed under the GNU Affero General Public License v3.0. " +
                     "You may obtain a copy at https://www.gnu.org/licenses/agpl-3.0.html"
        });
    }
}
