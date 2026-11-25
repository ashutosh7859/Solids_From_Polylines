# Development workflow

- This project is its own Git repository under `projects/Solids_From_Polylines`; do not run project commits from the parent workspace repository.
- Keep development on the `polyline-solids-fork` branch, which tracks `origin/polyline-solids-fork`.
- After completing and validating each requested change, commit the related source changes and push the branch to `origin/polyline-solids-fork`.
- Push over SSH only. The project-specific key is `~/.ssh/solids_from_polylines`; do not use the unrelated `~/.ssh/ai-dotfiles` key or switch the remote to HTTPS.
- From the parent workspace, run the push with the project key explicitly:
  `GIT_SSH_COMMAND="ssh -i $HOME/.ssh/solids_from_polylines -o IdentitiesOnly=yes" git -C projects/Solids_From_Polylines push origin polyline-solids-fork`
- Before committing, verify the project branch and review its status/diff. Stage only the files for the requested change. After pushing, verify `git -C projects/Solids_From_Polylines status -sb` shows the branch synchronized with origin.
- If SSH authentication fails, stop and resolve the project key/agent setup; do not fall back to HTTPS.
- Do not include generated `bin/` or `obj/` files in commits.
- Do not merge into `main` unless explicitly requested.
