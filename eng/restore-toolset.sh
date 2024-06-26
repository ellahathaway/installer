function InitializeCustomSDKToolset {
  if [[ "$restore" != true ]]; then
    return
  fi

  #InstallDotNetSharedFramework "1.0.5"
  #InstallDotNetSharedFramework "1.1.2"

  InitializeDotNetCli true
}

# Installs additional shared frameworks for testing purposes
function InstallDotNetSharedFramework {
  local version=$1
  local dotnet_root=$DOTNET_INSTALL_DIR
  local fx_dir="$dotnet_root/shared/Microsoft.NETCore.App/$version"

  if [[ ! -d "$fx_dir" ]]; then
    GetDotNetInstallScript "$dotnet_root"
    local install_script=$_GetDotNetInstallScript

    bash "$install_script" --version $version --install-dir "$dotnet_root" --runtime "dotnet" || true
  fi
}

InitializeCustomSDKToolset