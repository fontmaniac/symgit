# symgit

## Symbolic Git submodules

Ever thought of an idea to have Git submodules "swappable"? If only the linking between "parent" and "sub" wasn't that "hard" - utilising immutable revision hash which isn't in any way controllable. 

Enter **symgit** - a small script which implements a workflow more or less isomorphic to a classic `git submodule`, but with a twist. The "hash" which links "parent" and "sub" revisions is in the .manifest file which is under version control on both sides of the link. This way the "symbolic submodule" repository can be swapped with another which is not necessarily a clone of initial one, but just happens to have revisions containing compatible manifest.

## How it works (usage)

For each "symbolic submodule" **symgit** initializes a "manifest" file on both sides of the link.
Say, you have a project which includes a substantial collection of "assets" which you'd like to, potentially, swap around. For example, to let every developer to work with their own set of assets. Let's say the assets reside in the `./assets/personal` folder, relative to the project repo root. 

### Installation

We would add `symgit.fsx` to the root of project repo, and make sure it travels with the repo by committing it in. 

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
Effectively moving `/assets/personal` folder out of project's repo and into its own.


### `commit`

Time to make our first commit in the "submodule":
`dotnet fsi symgit.fsx commit volatile-assets.symgit -tag master -msg "Commit message"`
Or 
`dotnet fsi symgit.fsx commit -all -tag master`

The former command only processes the `volatile-assets.symgit` link. The latter seeks all `*.symgit` files in project's repo root and processes each of the found.
What does `symgit commit` do?
It checks linked sub-repo for any staged or unstaged changes, and if nothing is found - it does nothing. If there **are** changes, **symgit** compiles a *"manifest"* which consists of a **"manifest Id"** and a list of changed files derived from `git status --porcelain`. The **manifest Id** and said list of changes are then written into `.manifest` file in the root of sub-repo and into "link" file (`volatile-assets.symgit` in our example) in the parent repo. `symgit commit` then proceeds to stage all changes in sub-repo and commit them with commit message passed as `-msg`.

This way, by having the same **manifest Id** in both `volatile-assets.symgit` file and sub-repo's `.manifest` the effective "symbolic link" is established. You are free to continue working on both sides, running `symgit commit` periodically - which would modify the link manifests each time it detects changes. When you make a commit in the parent repo the link between created revision and corresponding revision in sub-repo will be preserved. 

### `checkout`

Third part of the primary command triad `init`-`commit`-`checkout`.
When a revision of the parent repo is checked out, naturally it is expected to have sub-repo checked out at corresponding revision too. For that we would run `symgit checkout`:
`dotnet fsi symgit.fsx checkout volatile-assets.symgit`
Or
`dotnet fsi symgit.fsx checkout -all`

The command will, for relevant `.symgit` link, run in the sub-repo `git checkout <hash>` where `<hash>` is git revision hash of a chronologically first sub-repo commit containing `.manifest` file having **manifest Id** matching one found in currently checked out `.symgit` file. That's all it is to it. 


### `update`

When parent repository is cloned in another location it is necessary to bring the associated symbolic submodules to the relevant revision. Or when sub-repo has been messed with and accidentally fixed beyond repair, and thus went into liquidation. This is what `symgit update` is for.

`dotnet fsi symgit.fsx update volatile-assets.symgit`
Or
`dotnet fsi symgit.fsx update -all`

The command will, for relevant `.symgit` link, run in the sub-repo `git pull` if `.git` already present, or `git clone <remote> .` if sub-repo hasn't yet been initialized or cloned. `<remote>` is the third line in `.symgit` manifest file. 

### `move`

It is perfectly possible and valid to rearrange location of "symbolic" sub-repo within parent repository without breaking the linkage. However one needs to remember to perform a few critical steps. `symgit move` helper is provided to simplify the process.

`dotnet fsi symgit.fsx move volatile-assets.symgit -repo assets/even-more-personal -remote ~/my-private-assets`

For obvious reasons the command doesn't accept `-all` flag. In its action it is isomorphic to `symgit init`, albeit it assumes that the `.git` in the linked sub-repo does exist (although it does not verify it). The result of successful `move` is folder pointed to by `.symgit` manifest copied into new location specified by `-repo` option, and parent's `.gitignore` modified accordingly. `.symgit` manifest file is updated too. If optional `-remote` flag is present the third line in the manifest is updated with new address.

### `prepare`

To enable use in hooks without sacrificing ability to synchronise commit message across the linked sides the elegant singular `symgit commit` has been split into ugly `prepare` and `commitOnly` - to be called from `pre-commit` and `prepare-commit-msg` hooks respectively. 

`dotnet fsi symgit.fsx prepare volatile-assets.symgit -tag master`
Or 
`dotnet fsi symgit.fsx prepare -all -tag master`

This will perform the first stage of `commit` command - prepare the manifests and stage changes in linked sub-repo. 

### `commitOnly`

`dotnet fsi symgit.fsx commitOnly volatile-assets.symgit -msg "Commit message"`
Or 
`dotnet fsi symgit.fsx commitOnly -all -msg "Commit message"`

This will perform the second stage of `commit` command - commit the changes staged in linked sub-repo. 

## Hooks

To streamline the workflow I found it useful to enable running `commit` through git hooks configured on parent repository. The bash scripts reside in the `hooks` folder of this repo. As described above, Git invokes commit hooks in split way, where first phase doesn't have access to commit message while second phase unable to modify the index in the context of ongoing operation. Hence the need for separate `prepare` and `commitOnly` commands. `Post-checkout` hook is also provided, but I found it is not as  useful for day-to-day working.

### Installation

Something unexpected happened at the time of writing this. The very moment I wanted to design a good, idempotent way of installing and uninstalling git hooks for symgit, my attention fell on [GitHub's announcement](https://github.blog/open-source/git/highlights-from-git-2-54/) about roll-out of `git 2.54`. As if by miracle the roll-out included long-awaited functionality of sane hook management through config. Hooks are now composable and can travel with the repo. The announcement filled me with joy, but also killed my enthusiasm for reinventing the wheel which was practically made obsolete now. So, to install the provided hooks you just plop them in the `.git/hooks` - this is what I do - or use any of the other methods available and convenient to you. 



## Why this was made (use-cases)

As far as I can see the limited functionality provided by **symgit** is quite well covered by wonderful [git-annex](https://git-annex.branchable.com/) tool, and most users will probably be better served by that versatile and battle-tested software. 
My personal foray into this space was triggered by the need to decouple "content" from a game project I was working on. At some point I wanted to publish my "game" as public repository on GitHub, but realized that some content, for various reasons, is better be replaced with alternate versions. I researched `git-annex` and concluded that it would be a perfect tool for the task. But my Windows OS disagreed. While `git-annex` worked, from the get-go it declared NTFS a "crippled file system" and devolved into a very "defensive" mode, creating weirdly named branches all over the place. I do not even disagree with such assessment! But that proliferation of branches crippled my ability to reason about what was going on. Hence I decided to make my own tooling, from which the idea of "symbolic submodules" emerged as more or less isolated concept. At some point I may publish the other tool I made for this exercise, in which case I may update this Readme file.

## How this was made (clunker disclaimer)

These days everything seems to be made with "AI", does it not? Raise your hand who, when faced with a need for a little, potentially one-off or otherwise private script, hadn't attempted to YOLO it by your favorite chatbot or "agent"? I am no different in this regard, but with one important caveat: should this YOLO-ing succeeded I wouldn't think of opening the result - as it would breach my conviction of never feeding LLM slop to human beings. So, why publishing now? Because YOLO-ing didn't quite work, of course!

I started with rubber-ducking my vision for this tool through Microsoft Copilot, which sometimes allows  useful insights and refinements. This time instead Copilot seemed to insist on cosplaying Mr.Wolf bossing around Vincent and Jules when they needed to have their bloodied basement cleaned. It "reframed" my vision back to me in the form of *"you take the mop and clean the floor, you take vacuum and do the seats"*. Really?! Is this what I'm "paying" you for, Mr.Clunker? Clearly motivated by revenge I tasked ~~Mr.Wolf~~ Copilot with generating code for me, step by step. To my surprise, it fell quite flat. F# is a great language which allows to very easily assess the "shape" of the code. So, the "shape" of Copilot's code was mostly all right, but either didn't compile due to syntax blunders or contained subtle bugs. As a result I would have to pull Mr.Copilot up from the floor, dusted of its expensive suit, had it seated in comfy lounge chair and provide for its entertainment with coffee and biscuits to relax and prepare for the next round, while I was getting my entertainment by interrrrogating (how many r's?) the code line-by-line, fully comprehending it and sometimes fully rewriting. 

Some may argue that laundering LLM slop through human rewriting by hand may not be possible. I agree that the code I am presenting may still be called "slop" - but I take my full human responsibility for it, as this "slop" is mine now! 

## License and Contribution

Please strictly adhere to terms and conditions outlined in the [LICENSE](LICENSE.txt). While code provided "AS IS" and comes with no warranties or guarantees whatsoever, I want to say the following. I have personally used this software to work through the projects valuable to me - and experienced no loss. On Windows and on Linux alike. It was and is useful to me - I use it on most my game projects. I want it to be useful for you too. However, I cannot say that I've extensively tested it in all possible scenarios. So, my advice if you want to use it: read the code and understand what exactly it does. It is compact and very tractable. If I could write and understand it, you could too! If it bugs out, I am sorry - but most likely I won't have time to fix the bug. Open an issue, and/or better yet, send a PR. Or don't. Wrangle your favorite LLM to fix it for you. 

**Do with this whatever you want - you have full power.**