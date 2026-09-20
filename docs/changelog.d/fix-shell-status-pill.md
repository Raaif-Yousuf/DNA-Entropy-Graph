- Fixed #424 and the status pill's own resource-key bug: `IStringResourceProvider.GetString`
  is called from code with keys like `StatusPillSignedIn.Text`, but a `.resw` entry named
  `Foo.Text` compiles into the PRI as the nested resource path `Foo/Text` -- the convention
  `x:Uid` relies on for a XAML binding, which a literal-string code lookup has no way to use.
  Every key reached only from code (`StatusPillSignedIn`, `StatusPillNotSignedIn`,
  `ConfirmDeleteResource_Title`/`_Body`, `NoRunsYet_Title`/`_Body`, `ThemeUpdated_Title`,
  `ConfirmStopVm_Title`/`_Body`, `ConfirmDeleteVm_Title`/`_Body`) is now the plain, non-dotted
  form the `Phase*_Title` keys already used; keys also reached via `x:Uid`
  (`ShellTitle.Text`, `NavNewRun.Content`, ...) keep the dotted form.
- Fixed #424's own bug: `FakeGcp` defaulted to signed in with `fake-project` selected, so a
  fresh profile's status pill never actually read "Not signed in" against the fake. The
  default is now signed out with no selected project; `WithSelectedProject` arms a
  signed-in fake explicitly (`WithSignedOut` restates the default for a test that depends
  on it). `SignInAsync` now actually flips the fake to signed in (to `fake-project`) instead
  of no-opping, per issue #424's own "Done when" -- `WizardViewModel.SignInAsync` reads
  `IGcpAccount.IsSignedIn` right after awaiting this call, so a no-op left that flow wired
  to nothing against this fake.
- Added `DnaEntropyGraph.Guards.Tests.StringResourceKeyGuardTests`: scans every C#
  `strings.GetString("...")` call site against the real `Resources.resw` and fails if a
  looked-up key is dotted or has no matching entry, so this bug class cannot recur silently.
- Moved the status pill out from under the window caption buttons. With a 16 DIP right
  margin it sat directly beneath the minimise, maximise and close buttons that the system
  draws over an `ExtendsContentIntoTitleBar` window, so "Not signed in" and the close
  button overlapped on screen. The three buttons are 46 DIP wide each, so a 154 DIP margin
  clears them with a 16 DIP gap and holds at any display scale because the margin is in
  DIPs.
