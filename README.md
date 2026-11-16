# Alidade

Alidade 是 Torquetum 的 Grasshopper 外掛專案，目標是在 Rhino／Grasshopper 和 Torquetum 專案之間交換幾何與資料樹。

Alidade is a Grasshopper plugin project for exchanging geometry and data trees with Torquetum.

## 本機建置

從儲存庫根目錄使用 .NET SDK 7 以上版本執行下列指令。NuGet 會取得固定版本的 Rhino 8.6 SDK，執行外掛時由 Rhino 提供 Grasshopper 和 RhinoCommon。

```sh
dotnet build src/Alidade/Alidade.csproj --configuration Release
```

建置產物位於 `src/Alidade/bin/Release/net7.0/Alidade.gha`，目標執行環境為 Rhino 8（.NET Core 模式）。

## 安裝和範例

將建置產物 `Alidade.gha` 複製到 Grasshopper 載入第三方元件的資料夾，或把建置目錄加入 Rhino 的 `GrasshopperDeveloperSettings`。重新啟動 Rhino，開啟 Grasshopper，從 **Alidade** 分類加入元件。Rhino 與 Torquetum Desktop 或本機 Web 需在同一台電腦執行。 [McNeel Grasshopper 組件目錄說明](https://developer.rhino3d.com/api/gh1/type/grasshopper.folders/)

1. 在 Asterism 放置 **Geometry Input** 和 **Geometry Output**，接通中間的 `Geometry`。
2. 在 Torquetum 的 **Project Settings** 按 **Copy Grasshopper pairing code**，再按 Grasshopper 的 **Pair with Torquetum**。若出現視窗，貼上配對碼。把 `Project` 接到兩個 Select、Send 與 Read。
3. 從兩個 **Select Asterism Port** 的 **Asterism ports** 選單分別選取 Input 和 Output。將 Input 接到 **Send Geometry**，將 Output 接到 **Read Geometry**。
4. 透過 **Build Geometry Collection** 將 Rhino 幾何接入 Send，使用 **Send** 或按下接至其 `Send` 腳位的 Grasshopper Button。把 Send 的 `Receipt` 接到 Read 可取得指定寫入的結果，把 Read 的 `Collection` 接到 **Unpack Geometry Collection** 取回幾何。

Build 和 Unpack 支援點、線、折線、平面、盒與網格。v2 另支援 STEP 面、殼與實體。轉換可離線操作，省略長度單位時使用 Rhino 模型單位。`Geometry IDs`（`K`）和 `Geometry Names`（`N`）是與幾何對齊的文字樹。重排時保留原 ID，名稱可修改。省略 ID 時由 Torquetum 配發，省略名稱時顯示 ID。

重新開檔後需配對。若其他用戶端更新了 Input，在 Select 執行 **Refresh list**。送出待確認時用 **Check result** 或 **Retry same request**。要將幾何儲存到 Station，請在 Asterism 執行 Bake。

## 本機驗收

在 macOS 可使用已安裝的 Rhino 8.6 與其 .NET 7 執行環境，檢查 v1、v2 契約、STEP 檔頭處理和實際 Grasshopper SDK 的部分資料樹往返：

```sh
dotnet build src/Alidade/Alidade.csproj -c Release --warnaserror
dotnet build tests/Alidade.Checks/Alidade.Checks.csproj -c Release --warnaserror
alidade_rhino_resources="/Applications/Rhino 8.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources"
"$alidade_rhino_resources/dotnet/$(uname -m)/dotnet" tests/Alidade.Checks/bin/Release/net7.0/Alidade.Checks.dll \
  src/Alidade/bin/Release/net7.0/Alidade.gha contracts/alidade/v1/fixtures/geometry-collection.json
```

Intel Mac 使用 Rhino 目錄中的 `x86_64` 執行環境，Apple Silicon 使用 `arm64`。驗收程式會載入外掛組件並檢查託管的 Grasshopper 資料樹和共用契約。實際元件接線、Rhino STEP 匯入及匯出仍待 Rhino 視窗驗收。

同一驗收程式加上 `--exchange` 可驗真實的本機 Torquetum API。請準備尚未收到資料的測試專案，其中一個 **Geometry Input** 直接接到唯一的 **Geometry Output**，並從標準輸入送入該專案的讀寫邀請。驗收涵蓋首次傳送、相同請求重試、資料樹讀回和拒絕過期版本。若已協商 v2，也會傳送並讀回合成的 STEP 面、殼與實體。

## 交換契約

配對、交易、版本協商與 JSON Schema 由 Torquetum 的 `contracts/alidade` 維護。 [Torquetum 交換契約](https://github.com/Avery320/torquetum/pull/26/files)

本機驗收使用同版的 [v1 幾何測試資料](contracts/alidade/v1/fixtures/geometry-collection.json)與 [v2 STEP 測試資料](contracts/alidade/v2/fixtures/geometry-collection.json)。

## 授權

本專案採用 [MIT License](LICENSE)。
