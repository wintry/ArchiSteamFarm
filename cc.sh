#!/bin/bash
set -eu

MAIN_PROJECT="ArchiSteamFarm"
TESTS_PROJECT="${MAIN_PROJECT}.Tests"
SOLUTION="${MAIN_PROJECT}.sln"
CONFIGURATION="Release"
OUT="out/source"

CLEAN=0
LINK_DURING_PUBLISH=1
PULL=1
SHARED_COMPILATION=1
TEST=1

cd "$(dirname "$(readlink -f "$0")")"

for ARG in "$@"; do
	case "$ARG" in
		debug|Debug) CONFIGURATION="Debug" ;;
		release|Release) CONFIGURATION="Release" ;;
		--clean) CLEAN=1 ;;
		--no-clean) CLEAN=0 ;;
		--link-during-publish) LINK_DURING_PUBLISH=1 ;;
		--no-link-during-publish) LINK_DURING_PUBLISH=0 ;;
		--pull) PULL=1 ;;
		--no-pull) PULL=0 ;;
		--shared-compilation) SHARED_COMPILATION=1 ;;
		--no-shared-compilation) SHARED_COMPILATION=0 ;;
		--test) TEST=1 ;;
		--no-test) TEST=0 ;;
		--help) echo "Usage: $0 [--clean] [--no-link-during-publish] [--no-pull] [--no-shared-compilation] [--no-test] [debug/release]"; exit 0 ;;
		*) echo "Usage: $0 [--clean] [--no-link-during-publish] [--no-pull] [--no-shared-compilation] [--no-test] [debug/release]"; exit 1
	esac
done

trap "trap - SIGTERM && kill -- -$$" SIGINT SIGTERM

if ! hash dotnet 2>/dev/null; then
	echo "ERROR: dotnet CLI tools are not installed!"
	exit 1
fi

dotnet --info

if [[ "$PULL" -eq 1 && -d ".git" ]] && hash git 2>/dev/null; then
	git pull || true
fi

if [[ ! -f "$SOLUTION" ]]; then
	echo "ERROR: $SOLUTION could not be found!"
	exit 1
fi

DOTNET_FLAGS=(-c "$CONFIGURATION" -o "$OUT" '/nologo')

if [[ "$LINK_DURING_PUBLISH" -eq 0 ]]; then
	DOTNET_FLAGS+=('/p:LinkDuringPublish=false')
fi

if [[ "$SHARED_COMPILATION" -eq 0 ]]; then
	DOTNET_FLAGS+=('/p:UseSharedCompilation=false')
fi

if [[ "$CLEAN" -eq 1 ]]; then
	dotnet clean "${DOTNET_FLAGS[@]}"
	rm -rf "${MAIN_PROJECT:?}/${OUT}" "${TESTS_PROJECT:?}/${OUT}"
fi

if [[ "$TEST" -eq 1 ]]; then
	dotnet test "$TESTS_PROJECT" "${DOTNET_FLAGS[@]}"
fi

dotnet publish "$MAIN_PROJECT" "${DOTNET_FLAGS[@]}"

echo
echo "Compilation finished successfully! :)"
