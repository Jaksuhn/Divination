import json
import os
from datetime import datetime, timedelta, timezone
from zipfile import ZipFile


def extract_manifests(env):
    manifests = {}
    if not os.path.isdir(f"docs/plugins/{env}"):
        return manifests

    for dir_path, _, filenames in os.walk(f"docs/plugins/{env}"):
        if "latest.zip" not in filenames:
            continue

        plugin_name = dir_path.split("/")[-1]
        if os.path.exists(f"{dir_path}/{plugin_name}.json"):
            with open(f"{dir_path}/{plugin_name}.json") as f:
                manifest = json.load(f)
                manifests[manifest["InternalName"]] = manifest
        else:
            with ZipFile(f"{dir_path}/latest.zip") as z:
                manifest = json.loads(z.read(f"{plugin_name}.json").decode())
                manifests[manifest["InternalName"]] = manifest

    return manifests


def get_changelog(path):
    commits_path = f"{path}/commits.json"
    if not os.path.exists(commits_path):
        return None

    with open(commits_path) as f:
        commits = json.load(f)

    if not isinstance(commits, list):
        return None

    return "\n".join([
        f"{x['sha'][:7]}: {x['commit']['message']}"
        for x in commits
        if x["commit"]["author"]["name"] != "github-actions"
    ]) or None


def get_repo_url(path):
    event_path = f"{path}/event.json"
    if not os.path.exists(event_path):
        return None

    with open(event_path) as f:
        event = json.load(f)

    if "repository" in event:
        return event["repository"]["html_url"]

    return None


def get_last_updated(path):
    event_path = f"{path}/event.json"
    if not os.path.exists(event_path):
        zip_path = f"{path}/latest.zip"
        if not os.path.exists(zip_path):
            return 0

        return int(os.path.getmtime(zip_path))

    with open(event_path) as f:
        event = json.load(f)

    # on: push
    if "head_commit" in event:
        timestamp = event["head_commit"]["timestamp"]
    # on: release
    elif "created_at" in event:
        timestamp = event["created_at"]
    # on: workflow_dispatch
    else:
        commits_path = f"{path}/commits.json"
        with open(commits_path) as f:
            commits = json.load(f)
        timestamp = commits[0]["commit"]["author"]["date"]

    try:
        epoch = datetime.fromisoformat(timestamp)
    except ValueError:
        epoch = datetime.strptime(timestamp, "%Y-%m-%dT%H:%M:%SZ")
    return int(epoch.timestamp())


def merge_manifests(stable, testing):
    manifest_keys = set(list(stable.keys()) + list(testing.keys()))

    manifests = []
    for key in manifest_keys:
        stable_path = f"docs/plugins/stable/{key}"
        stable_manifest = stable.get(key, {})
        stable_link = f"https://xiv.starry.blue/plugins/stable/{key}/latest.zip"
        testing_path = f"docs/plugins/testing/{key}"
        testing_manifest = testing.get(key, {})
        testing_link = f"https://xiv.starry.blue/plugins/testing/{key}/latest.zip"

        manifest = testing_manifest.copy() if testing_manifest else stable_manifest.copy()

        manifest["Changelog"] = get_changelog(testing_path) or get_changelog(stable_path)
        manifest["IsHide"] = testing_manifest.get("IsHide", stable_manifest.get("IsHide", False))
        manifest["RepoUrl"] = testing_manifest.get("RepoUrl", stable_manifest.get("RepoUrl")) or get_repo_url(testing_path) or get_repo_url(stable_path)
        manifest["AssemblyVersion"] = stable_manifest["AssemblyVersion"] if stable_manifest else testing_manifest["AssemblyVersion"]
        manifest["TestingAssemblyVersion"] = testing_manifest["AssemblyVersion"] if testing_manifest else None
        manifest["IsTestingExclusive"] = not bool(stable_manifest) and bool(testing_manifest)
        manifest["LastUpdated"] = max(get_last_updated(stable_path), get_last_updated(testing_path))
        manifest["DownloadLinkInstall"] = stable_link if stable_manifest else testing_link
        manifest["DownloadLinkTesting"] = testing_link if testing_manifest else stable_link
        manifest["IconUrl"] = testing_manifest.get("IconUrl", stable_manifest.get("IconUrl"))

        manifests.append(manifest)

    return manifests


def dump_master(manifests):
    manifests.sort(key=lambda x: x["InternalName"])

    with open("docs/plugins/master.json", "w") as f:
        json.dump(manifests, f, indent=2, sort_keys=True)


def generate_markdown(manifests):
    lines = [
        "# Divination Plugins",
        "",
        "## Legend",
        "",
        "⚠️ = Testing/very experimental plugin. May cause game crashes or other inconveniences.",
        "",
        "## Plugin List",
        "",
        "| Name | Version | Author | Description |",
        "|:-----|:-------:|:------:|:------------|"
    ]

    jst = timezone(timedelta(hours=9))

    for manifest in manifests:
        if manifest["IsHide"]:
            continue

        name = f"[{manifest['Name']}]({manifest['RepoUrl']})"

        stable_version = f"**[{manifest['AssemblyVersion']}]({manifest['DownloadLinkInstall']})**" if manifest["DownloadLinkInstall"] != manifest["DownloadLinkTesting"] else "-"
        testing_version = f"⚠️ [{manifest['TestingAssemblyVersion']}]({manifest['DownloadLinkTesting']})" if manifest["TestingAssemblyVersion"] else "-"
        last_updated = datetime.fromtimestamp(manifest["LastUpdated"], tz=jst).strftime("%Y-%m-%d")
        version = f"{stable_version} / {testing_version} ({last_updated})"

        author = manifest["Author"]

        tags = [fr"**\#{x}**" for x in manifest.get("CategoryTags", []) + manifest.get("Tags", [])]
        description = f"{manifest.get('Punchline', '-')}<br>{manifest.get('Description', '-')}<br>{' '.join(tags)}"

        lines.append(f"| {name} | {version} | {author} | {description} |")

    with open("docs/plugins/README.md", "w") as f:
        f.write("\n".join(lines))


if __name__ == "__main__":
    stable_manifests = extract_manifests("stable")
    testing_manifests = extract_manifests("testing")
    merged_manifests = merge_manifests(stable_manifests, testing_manifests)

    dump_master(merged_manifests)
    generate_markdown(merged_manifests)