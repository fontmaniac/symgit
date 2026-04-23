# symgit

## Symbolic Git submodules

Ever thought of an idea to have Git submodules "swappable"? If only the linking between "parent" and "sub" wasn't that "hard" - utilising immutable revision hash which isn't in any way controllable. 

Enter **symgit** - a small script which implements a workflow more or less isomorphic to a classic `git submodule`, but with a twist. The "hash" which links "parent" and "sub" revisions is in the .manifest file which is under version control on both sides of the link. This way the "symbolic submodule" repository can be swapped with another which is not necessarily a clone of initial one, but just happens to have revisions containing compatible manifest.

## How it works / usage

For each "symbolic submodule" **symgit** initializes a "manifest" file on both sides of the link.
Say, you have a project which includes a substantial collection of "assets" which you'd like to, potentially, swap around. For example, to let every developer to work with their own set of assets. Let's say the assets reside in the `./assets/personal` folder, relative to the project repo root. 

### `init`

We would `init` the "symbolic submodule" with the following command:

`dotnet fsi symgit.fsx init volatile-assets.symgit -repo assets/personal -remote ~/my-assets`

The command will perform the following:
- Create file `volatile-assets.symgit` with three text lines:
```


assets/personal
~/my-assets
```
Note that the first line is intentionally left blank - there is no "revision link" established at this point, only "location link"

- Initialize an empty git repo in `assets/personal/.git`
- Add to project's `.gitignore` the lines:
```
/assets/personal/*
/assets/personal/
```
Effectively moving `/assets/personal` folder out of project's repo and into it's own.


### `commit`

Time to make our first commit in the "submodule":
`dotnet fsi symgit.fsx commit volatile-assets.symgit -tag master -msg "Commit message"`
Or 
`dotnet fsi symgit.fsx commit -all -tag master`

The former command only processes the `volatile-assets.symgit` link. The latter seeks all `*.symgit` files in project's repo root and processes each of the found.
What does `symgit commit` do?
It checks linked sub-repo for any staged or unstaged changes, and if nothing is found - it does nothing. If there **are** changes, **symgit** compiles a *"manifest"* which consists of a **"manifest Id"** and a list of changed files derived from `git status --porcelain`. The **manifest Id** and said list of changes are then written into `.manifest` file in the root of sub-repo and into "link" file (`volatile-assets.symgit` in our example) in the parent repo. `symgit commit` then proceeds to stage all changes in sub-repo and commit them with commit message passed as `-msg`.

This way, by having the same **manifest Id** in both `volatile-assets.symgit` file and sub-repo's `.manifest` the effective "symbolic link" is established. You a free to continue working on both sides, running `symgit commit` periodically - which would modify the link manifests each time it detects changes. When you make a commit in the parent repo the link between created revision and corresponding revision in sub-repo will be preserved. 

### `checkout`

Third part of the primary command triad `init`-`commit`-`checkout`.
When a revision of the parent repo is checked out, naturally it is expected to have sub-repo checked out at corresponding revision too. For that we would run `symgit checkout`:
`dotnet fsi symgit.fsx checkout volatile-assets.symgit`
Or
`dotnet fsi symgit.fsx checkout -all`

The command will, for relevant `.symgit` link, run in the sub-repo `git checkout <hash>` where `<hash>` is git revision hash of a chronologically first sub-repo commit containing `.manifest` file having **manifest Id** matching one found in currently checked out `.symgit` file. That's all it is to it. 

