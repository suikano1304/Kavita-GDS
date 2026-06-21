#!/usr/bin/env bash
set -euo pipefail

base_ref="${1:-upstream/develop}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
patch_dir="${TMPDIR:-/tmp}/kavita-gds-patch-stack-selftest-${stamp}"
worktree_dir="${TMPDIR:-/tmp}/kavita-gds-apply-selftest-${stamp}"
branch_name="gds-apply-selftest-${stamp}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cleanup() {
  git -C "${script_dir}/.." worktree remove --force "${worktree_dir}" >/dev/null 2>&1 || true
  git -C "${script_dir}/.." branch -D "${branch_name}" >/dev/null 2>&1 || true
  rm -rf "${patch_dir}"
}
trap cleanup EXIT

cd "${script_dir}/.."
git rev-parse --is-inside-work-tree >/dev/null

echo "== syntax =="
bash -n \
  "${script_dir}/gds_patch_stack_summary.sh" \
  "${script_dir}/gds_export_patch_stack.sh" \
  "${script_dir}/gds_apply_patch_stack.sh" \
  "${script_dir}/gds_porting_selftest.sh"

echo "== summary =="
"${script_dir}/gds_patch_stack_summary.sh" "${base_ref}" | tail -12

echo "== export =="
"${script_dir}/gds_export_patch_stack.sh" "${base_ref}" "${patch_dir}"

echo "== apply =="
apply_output="$("${script_dir}/gds_apply_patch_stack.sh" "${patch_dir}" "${worktree_dir}" "${base_ref}" "${branch_name}")"
printf '%s\n' "${apply_output}"
if ! grep -q '^tree_match=PASS$' <<<"${apply_output}"; then
  echo "Expected tree_match=PASS in apply output" >&2
  exit 1
fi

echo "== result =="
echo "selftest=PASS"
