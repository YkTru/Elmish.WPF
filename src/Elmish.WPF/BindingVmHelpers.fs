namespace Elmish.WPF

open System
open System.Windows
open Microsoft.Extensions.Logging

open Elmish


type internal UpdateData =
  | ErrorsChanged of string
  | PropertyChanged of string
  | CanExecuteChanged of Command

module internal UpdateData =
  let isPropertyChanged = function PropertyChanged _ -> true | _ -> false


type internal GetErrorSubModelSelectedItem =
  { NameChain: string
    SubModelSeqBindingName: string
    Id: string }

[<RequireQualifiedAccess>]
type internal GetError =
  | OneWayToSource
  | SubModelSelectedItem of GetErrorSubModelSelectedItem


module internal Helpers2 =
  let showNewWindow
      (winRef: WeakReference<Window>)
      (getWindow: 'model -> Dispatch<'msg> -> Window)
      (isDialog: bool)
      (onCloseRequested: 'model -> 'msg voption)
      (preventClose: bool ref)
      dataContext
      (initialVisibility: Visibility)
      (getCurrentModel: unit -> 'model)
      (dispatch: 'msg -> unit) =
    let win = getWindow (getCurrentModel ()) dispatch
    winRef.SetTarget win
    (*
     * A different thread might own this Window, so must use its Dispatcher.
     * Invoking asynchronously since ShowDialog is a blocking call. Otherwise,
     * invoking ShowDialog synchronously blocks the Elmish dispatch loop.
     *)
    win.Dispatcher.InvokeAsync(fun () ->
      win.DataContext <- dataContext
      win.Closing.Add(fun ev ->
        ev.Cancel <- preventClose.Value
        getCurrentModel () |> onCloseRequested |> ValueOption.iter dispatch
      )
      if isDialog then
        win.ShowDialog () |> ignore
      else
        (*
         * Calling Show achieves the same end result as setting Visibility
         * property of the Window object to Visible. However, there is a
         * difference between the two from a timing perspective.
         *
         * Calling Show is a synchronous operation that returns only after
         * the Loaded event on the child window has been raised.
         *
         * Setting Visibility, however, is an asynchronous operation that
         * returns immediately
         * https://docs.microsoft.com/en-us/dotnet/api/system.windows.window.show
         *)
        win.Visibility <- initialVisibility
    ) |> ignore

  let measure (logPerformance: ILogger) (performanceLogThresholdMs: int) (name: string) (nameChain: string) (callName: string) f =
    if not <| logPerformance.IsEnabled(LogLevel.Trace) then f
    else
      fun a ->
        let sw = System.Diagnostics.Stopwatch.StartNew ()
        let b = f a
        sw.Stop ()
        if sw.ElapsedMilliseconds >= int64 performanceLogThresholdMs then
          logPerformance.LogTrace("[{BindingNameChain}] {CallName} ({Elapsed}ms): {MeasureName}", nameChain, callName, sw.ElapsedMilliseconds, name)
        b

  let measure2 (logPerformance: ILogger) performanceLogThresholdMs name nameChain callName f =
    if not <| logPerformance.IsEnabled(LogLevel.Trace)
    then f
    else fun a -> measure logPerformance performanceLogThresholdMs name nameChain callName (f a)


type internal OneWayBinding<'model, 'a> = {
  OneWayData: OneWayData<'model, 'a>
}

type internal OneWayToSourceBinding<'model, 'a> = {
  Set: 'a -> 'model -> unit
}

type internal OneWaySeqBinding<'model, 'a, 'b, 'id when 'id : equality> = {
  OneWaySeqData: OneWaySeqLazyData<'model, 'a, 'b, 'id> // TODO: consider renaming so that both contain "Lazy" or neither do
  Values: CollectionTarget<'b>
}

type internal TwoWayBinding<'model, 'a> = {
  Get: 'model -> 'a
  Set: 'a -> 'model -> unit
}

type internal SubModelBinding<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel> = {
  SubModelData: SubModelData<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel>
  Vm: 'bindingViewModel voption ref
}

type internal SubModelWinBinding<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel> = {
  SubModelWinData: SubModelWinData<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel>
  WinRef: WeakReference<Window>
  PreventClose: bool ref
  VmWinState: WindowState<'bindingViewModel> ref
}

type internal SubModelSeqUnkeyedBinding<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel> = {
  SubModelSeqUnkeyedData: SubModelSeqUnkeyedData<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel>
  Vms: CollectionTarget<'bindingViewModel>
}

type internal SubModelSeqKeyedBinding<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel, 'id when 'id : equality> =
  { SubModelSeqKeyedData: SubModelSeqKeyedData<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel, 'id>
    Vms: CollectionTarget<'bindingViewModel> }

  member d.FromId(id: 'id) =
    d.Vms.Enumerate ()
    |> Seq.tryFind (fun vm -> vm |> d.SubModelSeqKeyedData.GetUnderlyingModel |> d.SubModelSeqKeyedData.GetId |> (=) id)

type internal SelectedItemBinding<'bindingModel, 'bindingMsg, 'bindingViewModel, 'id> =
  { GetId: 'bindingModel -> 'id
    FromId: 'id -> 'bindingViewModel option
    GetUnderlyingModel: 'bindingViewModel -> 'bindingModel }

type internal SubModelSelectedItemBinding<'model, 'msg, 'bindingModel, 'bindingMsg, 'bindingViewModel, 'id> =
  { Get: 'model -> 'id voption
    Set: 'id voption -> 'model -> unit
    SubModelSeqBindingName: string
    SelectedItemBinding: SelectedItemBinding<'bindingModel, 'bindingMsg, 'bindingViewModel, 'id> }

  member d.TryGetMember (model: 'model) =
    d.Get model |> ValueOption.map (fun selectedId -> selectedId, d.SelectedItemBinding.FromId selectedId)

  member d.TrySetMember
      (model: 'model,
       bindingModel: 'bindingModel voption) =
    let id = bindingModel |> ValueOption.map d.SelectedItemBinding.GetId
    d.Set id model


type internal BaseVmBinding<'model, 'msg> =
  | OneWay of OneWayBinding<'model, obj>
  | OneWayToSource of OneWayToSourceBinding<'model, obj>
  | OneWaySeq of OneWaySeqBinding<'model, obj, obj, obj>
  | TwoWay of TwoWayBinding<'model, obj>
  | Cmd of cmd: Command
  | SubModel of SubModelBinding<'model, 'msg, obj, obj, obj>
  | SubModelWin of SubModelWinBinding<'model, 'msg, obj, obj, obj>
  | SubModelSeqUnkeyed of SubModelSeqUnkeyedBinding<'model, 'msg, obj, obj, obj>
  | SubModelSeqKeyed of SubModelSeqKeyedBinding<'model, 'msg, obj, obj, obj, obj>
  | SubModelSelectedItem of SubModelSelectedItemBinding<'model, 'msg, obj, obj, obj, obj>


type internal CachedBinding<'model, 'msg, 'value> = {
  Binding: VmBinding<'model, 'msg>
  Cache: 'value option ref
}

and internal ValidationBinding<'model, 'msg> = {
  Binding: VmBinding<'model, 'msg>
  Validate: 'model -> string list
  Errors: string list ref
}

and internal LazyBinding<'model, 'msg, 'bindingModel, 'bindingMsg> = {
  Binding: VmBinding<'bindingModel, 'bindingMsg>
  Get: 'model -> 'bindingModel
  Dispatch: 'bindingMsg -> unit
  Equals: 'bindingModel -> 'bindingModel -> bool
}

and internal AlterMsgStreamBinding<'model, 'bindingModel, 'bindingMsg> = {
  Binding: VmBinding<'bindingModel, 'bindingMsg>
  Get: 'model -> 'bindingModel
  Dispatch: 'bindingMsg -> unit
}

/// Represents all necessary data used in an active binding.
and internal VmBinding<'model, 'msg> =
  | BaseVmBinding of BaseVmBinding<'model, 'msg>
  | Cached of CachedBinding<'model, 'msg, obj>
  | Validatation of ValidationBinding<'model, 'msg>
  | Lazy of LazyBinding<'model, 'msg, obj, obj>
  | AlterMsgStream of AlterMsgStreamBinding<'model, obj, obj>

  with

    member this.AddCaching = Cached { Binding = this; Cache = ref None }
    member this.AddValidation currentModel validate =
      { Binding = this
        Validate = validate
        Errors = currentModel |> validate |> ref }
      |> Validatation


type internal SubModelSelectedItemLast() =

  member _.Base(data: BaseBindingData<'model, 'msg>) : int =
    match data with
    | SubModelSelectedItemData _ -> 1
    | _ -> 0

  member this.Recursive<'model, 'msg>(data: BindingData<'model, 'msg>) : int =
    match data with
    | BaseBindingData d -> this.Base d
    | CachingData d -> this.Recursive d
    | ValidationData d -> this.Recursive d.BindingData
    | LazyData d -> this.Recursive d.BindingData
    | AlterMsgStreamData d -> this.Recursive d.BindingData

  member this.CompareBindingDatas() : BindingData<'model, 'msg> -> BindingData<'model, 'msg> -> int =
    fun a b -> this.Recursive(a) - this.Recursive(b)

  member this.CompareBindings() : Binding<'model, 'msg> -> Binding<'model, 'msg> -> int =
    fun a b -> this.Recursive(a.Data) - this.Recursive(b.Data)



type internal FirstValidationErrors() =

  member this.Recursive<'model, 'msg>
      (binding: VmBinding<'model, 'msg>)
      : string list ref option =
    match binding with
    | BaseVmBinding _ -> None
    | Cached b -> this.Recursive b.Binding
    | Lazy b -> this.Recursive b.Binding
    | AlterMsgStream b -> this.Recursive b.Binding
    | Validatation b -> b.Errors |> Some // TODO: what if there is more than one validation effect?


type internal FuncsFromSubModelSeqKeyed() =

  member _.Base(binding: BaseVmBinding<'model, 'msg>) =
    match binding with
    | SubModelSeqKeyed b -> Some { GetId = b.SubModelSeqKeyedData.GetId; FromId = b.FromId; GetUnderlyingModel = b.SubModelSeqKeyedData.GetUnderlyingModel }
    | _ -> None

  member this.Recursive<'model, 'msg>
      (binding: VmBinding<'model, 'msg>)
      : SelectedItemBinding<obj, obj, obj, obj> option =
    match binding with
    | BaseVmBinding b -> this.Base b
    | Cached b -> this.Recursive b.Binding
    | Validatation b -> this.Recursive b.Binding
    | Lazy b -> this.Recursive b.Binding
    | AlterMsgStream b -> this.Recursive b.Binding


type internal Initialize
      (loggingArgs: LoggingViewModelArgs,
       name: string,
       getFunctionsForSubModelSelectedItem: string -> SelectedItemBinding<obj, obj, obj, obj> option) =

  let { log = log
        logPerformance = logPerformance
        performanceLogThresholdMs = performanceLogThresholdMs
        nameChain = nameChain } =
    loggingArgs

  let measure x = x |> Helpers2.measure logPerformance performanceLogThresholdMs name nameChain
  let measure2 x = x |> Helpers2.measure2 logPerformance performanceLogThresholdMs name nameChain

  member _.Base<'model, 'msg>
      (initialModel: 'model,
       dispatch: 'msg -> unit,
       getCurrentModel: unit -> 'model,
       binding: BaseBindingData<'model, 'msg>)
      : BaseVmBinding<'model, 'msg> option =
    match binding with
      | OneWayData d ->
          { OneWayData = d |> BindingData.OneWay.measureFunctions measure }
          |> OneWay
          |> Some
      | OneWayToSourceData d ->
          let d = d |> BindingData.OneWayToSource.measureFunctions measure
          { Set = fun obj m -> d.Set obj m |> dispatch }
          |> OneWayToSource
          |> Some
      | OneWaySeqLazyData d ->
          { OneWaySeqData = d |> BindingData.OneWaySeqLazy.measureFunctions measure measure measure2 measure measure2
            Values = d.CreateCollection (initialModel |> d.Get |> d.Map) }
          |> OneWaySeq
          |> Some
      | TwoWayData d ->
          let d = d |> BindingData.TwoWay.measureFunctions measure measure
          { Get = d.Get
            Set = fun obj m -> d.Set obj m |> dispatch }
          |> TwoWay
          |> Some
      | CmdData d ->
          let d = d |> BindingData.Cmd.measureFunctions measure2 measure2
          let execute param = d.Exec param (getCurrentModel ()) |> ValueOption.iter dispatch
          let canExecute param = d.CanExec param (getCurrentModel ())
          let cmd = Command(execute, canExecute)
          if d.AutoRequery then
            cmd.AddRequeryHandler ()
          cmd
          |> Cmd
          |> Some
      | SubModelData d ->
          let d = d |> BindingData.SubModel.measureFunctions measure measure measure2
          let toMsg = fun msg -> d.ToMsg (getCurrentModel ()) msg
          let chain = LoggingViewModelArgs.getNameChainFor nameChain name
          d.GetModel initialModel
          |> ValueOption.map (fun m -> ViewModelArgs.create m (toMsg >> dispatch) chain loggingArgs)
          |> ValueOption.map d.CreateViewModel
          |> (fun vm -> { SubModelData = d; Vm = ref vm })
          |> SubModel
          |> Some
      | SubModelWinData d ->
          let d = d |> BindingData.SubModelWin.measureFunctions measure measure measure2
          let toMsg = fun msg -> d.ToMsg (getCurrentModel ()) msg
          match d.GetState initialModel with
          | WindowState.Closed ->
              { SubModelWinData = d
                WinRef = WeakReference<_>(null)
                PreventClose = ref true
                VmWinState = ref WindowState.Closed }
          | WindowState.Hidden m ->
              let chain = LoggingViewModelArgs.getNameChainFor nameChain name
              let args = ViewModelArgs.create m (toMsg >> dispatch) chain loggingArgs
              let vm = d.CreateViewModel args
              let winRef = WeakReference<_>(null)
              let preventClose = ref true
              log.LogTrace("[{BindingNameChain}] Creating hidden window", chain)
              Helpers2.showNewWindow winRef d.GetWindow d.IsModal d.OnCloseRequested preventClose vm Visibility.Hidden getCurrentModel dispatch
              { SubModelWinData = d
                WinRef = winRef
                PreventClose = preventClose
                VmWinState = ref <| WindowState.Hidden vm }
          | WindowState.Visible m ->
              let chain = LoggingViewModelArgs.getNameChainFor nameChain name
              let args = ViewModelArgs.create m (toMsg >> dispatch) chain loggingArgs
              let vm = d.CreateViewModel args
              let winRef = WeakReference<_>(null)
              let preventClose = ref true
              log.LogTrace("[{BindingNameChain}] Creating visible window", chain)
              Helpers2.showNewWindow winRef d.GetWindow d.IsModal d.OnCloseRequested preventClose vm Visibility.Visible getCurrentModel dispatch
              { SubModelWinData = d
                WinRef = winRef
                PreventClose = preventClose
                VmWinState = ref <| WindowState.Visible vm }
          |> SubModelWin
          |> Some
      | SubModelSeqUnkeyedData d ->
          let d = d |> BindingData.SubModelSeqUnkeyed.measureFunctions measure measure measure2
          let toMsg = fun msg -> d.ToMsg (getCurrentModel ()) msg
          let vms =
            d.GetModels initialModel
            |> Seq.indexed
            |> Seq.map (fun (idx, m) ->
                 let chain = LoggingViewModelArgs.getNameChainForItem nameChain name (idx |> string)
                 let args = ViewModelArgs.create m (fun msg -> toMsg (idx, msg) |> dispatch) chain loggingArgs
                 d.CreateViewModel args)
            |> d.CreateCollection
          { SubModelSeqUnkeyedData = d
            Vms = vms }
          |> SubModelSeqUnkeyed
          |> Some
      | SubModelSeqKeyedData d ->
          let d = d |> BindingData.SubModelSeqKeyed.measureFunctions measure measure measure2 measure
          let toMsg = fun msg -> d.ToMsg (getCurrentModel ()) msg
          let vms =
            d.GetSubModels initialModel
            |> Seq.map (fun m ->
                 let mId = d.GetId m
                 let chain = LoggingViewModelArgs.getNameChainForItem nameChain name (mId |> string)
                 let args = ViewModelArgs.create m (fun msg -> toMsg (mId, msg) |> dispatch) chain loggingArgs
                 d.CreateViewModel args)
            |> d.CreateCollection
          { SubModelSeqKeyedData = d
            Vms = vms }
          |> SubModelSeqKeyed
          |> Some
      | SubModelSelectedItemData d ->
          let d = d |> BindingData.SubModelSelectedItem.measureFunctions measure measure2
          d.SubModelSeqBindingName
          |> getFunctionsForSubModelSelectedItem
          |> Option.map (fun selectedItemBinding ->
              { Get = d.Get
                Set = fun obj m -> d.Set obj m |> dispatch
                SubModelSeqBindingName = d.SubModelSeqBindingName
                SelectedItemBinding = selectedItemBinding }
              |> SubModelSelectedItem)

  member this.Recursive<'model, 'msg>
      (initialModel: 'model,
       dispatch: 'msg -> unit,
       getCurrentModel: unit -> 'model,
       binding: BindingData<'model, 'msg>)
      : VmBinding<'model, 'msg> option =
    option {
      match binding with
      | BaseBindingData d ->
          let! b = this.Base(initialModel, dispatch, getCurrentModel, d)
          return BaseVmBinding b
      | CachingData d ->
          let! b = this.Recursive(initialModel, dispatch, getCurrentModel, d)
          return b.AddCaching
      | ValidationData d ->
          let d = d |> BindingData.Validation.measureFunctions measure
          let! b = this.Recursive(initialModel, dispatch, getCurrentModel, d.BindingData)
          return b.AddValidation (getCurrentModel ()) d.Validate
      | LazyData d ->
          let initialModel' : obj = d.Get initialModel
          let getCurrentModel' : unit -> obj = getCurrentModel >> d.Get
          let dispatch' : obj -> unit = d.MapDispatch(getCurrentModel, dispatch)
          let d = d |> BindingData.Lazy.measureFunctions measure measure2 measure2
          let! b = this.Recursive(initialModel', dispatch', getCurrentModel', d.BindingData)
          return { Binding = b
                   Get = d.Get
                   Dispatch = dispatch'
                   Equals = d.Equals
                 } |> Lazy
      | AlterMsgStreamData d ->
          let initialModel' : obj = d.Get initialModel
          let getCurrentModel' : unit -> obj = getCurrentModel >> d.Get
          let dispatch' : obj -> unit = d.MapDispatch(getCurrentModel, dispatch)
          let! b = this.Recursive(initialModel', dispatch', getCurrentModel', d.BindingData)
          return { Binding = b
                   Get = d.Get
                   Dispatch = dispatch'
                 } |> AlterMsgStream
    }


/// Updates the binding and returns a list indicating what events to raise for this binding
type internal Update
    (loggingArgs: LoggingViewModelArgs,
     name: string) =

  let { log = log
        nameChain = nameChain } =
    loggingArgs

  member _.Base<'model, 'msg>
      (currentModel: 'model,
       newModel: 'model,
       dispatch: 'msg -> unit,
       binding: BaseVmBinding<'model, 'msg>) =
    match binding with
      | OneWay _
      | TwoWay _
      | SubModelSelectedItem _ -> [ PropertyChanged name ]
      | OneWayToSource _ -> []
      | OneWaySeq b ->
          b.OneWaySeqData.Merge(b.Values, currentModel, newModel)
          []
      | Cmd cmd -> cmd |> CanExecuteChanged |> List.singleton
      | SubModel b ->
        let d = b.SubModelData
        match b.Vm.Value, d.GetModel newModel with
        | ValueNone, ValueNone -> []
        | ValueSome _, ValueNone ->
            b.Vm.Value <- ValueNone
            [ PropertyChanged name ]
        | ValueNone, ValueSome m ->
            let toMsg = fun msg -> d.ToMsg currentModel msg
            let chain = LoggingViewModelArgs.getNameChainFor nameChain name
            let args = ViewModelArgs.create m (toMsg >> dispatch) chain loggingArgs
            b.Vm.Value <- ValueSome <| d.CreateViewModel(args)
            [ PropertyChanged name ]
        | ValueSome vm, ValueSome m ->
            d.UpdateViewModel (vm, m)
            []
      | SubModelWin b ->
          let d = b.SubModelWinData
          let winPropChain = LoggingViewModelArgs.getNameChainFor nameChain name
          let close () =
            b.PreventClose.Value <- false
            match b.WinRef.TryGetTarget () with
            | false, _ ->
                log.LogError("[{BindingNameChain}] Attempted to close window, but did not find window reference", winPropChain)
            | true, w ->
                log.LogTrace("[{BindingNameChain}] Closing window", winPropChain)
                b.WinRef.SetTarget null
                (*
                 * The Window might be in the process of closing,
                 * so instead of immediately executing Window.Close via Dispatcher.Invoke,
                 * queue a call to Window.Close via Dispatcher.InvokeAsync.
                 * https://github.com/elmish/Elmish.WPF/issues/330
                 *)
                w.Dispatcher.InvokeAsync(w.Close) |> ignore
            b.WinRef.SetTarget null

          let hide () =
            match b.WinRef.TryGetTarget () with
            | false, _ ->
                log.LogError("[{BindingNameChain}] Attempted to hide window, but did not find window reference", winPropChain)
            | true, w ->
                log.LogTrace("[{BindingNameChain}] Hiding window", winPropChain)
                w.Dispatcher.Invoke(fun () -> w.Visibility <- Visibility.Hidden)

          let showHidden () =
            match b.WinRef.TryGetTarget () with
            | false, _ ->
                log.LogError("[{BindingNameChain}] Attempted to show existing hidden window, but did not find window reference", winPropChain)
            | true, w ->
                log.LogTrace("[{BindingNameChain}] Showing existing hidden window", winPropChain)
                w.Dispatcher.Invoke(fun () -> w.Visibility <- Visibility.Visible)

          let showNew vm =
            b.PreventClose.Value <- true
            Helpers2.showNewWindow b.WinRef d.GetWindow d.IsModal d.OnCloseRequested b.PreventClose vm

          let newVm model =
            let toMsg = fun msg -> d.ToMsg currentModel msg
            let chain = LoggingViewModelArgs.getNameChainFor nameChain name
            let args = ViewModelArgs.create model (toMsg >> dispatch) chain loggingArgs
            d.CreateViewModel args

          match b.VmWinState.Value, d.GetState newModel with
          | WindowState.Closed, WindowState.Closed ->
              []
          | WindowState.Hidden vm, WindowState.Hidden m
          | WindowState.Visible vm, WindowState.Visible m ->
              d.UpdateViewModel (vm, m)
              []
          | WindowState.Hidden _, WindowState.Closed
          | WindowState.Visible _, WindowState.Closed ->
              close ()
              b.VmWinState.Value <- WindowState.Closed
              [ PropertyChanged name ]
          | WindowState.Visible vm, WindowState.Hidden m ->
              hide ()
              d.UpdateViewModel (vm, m)
              b.VmWinState.Value <- WindowState.Hidden vm
              []
          | WindowState.Hidden vm, WindowState.Visible m ->
              d.UpdateViewModel (vm, m)
              showHidden ()
              b.VmWinState.Value <- WindowState.Visible vm
              []
          | WindowState.Closed, WindowState.Hidden m ->
              let vm = newVm m
              log.LogTrace("[{BindingNameChain}] Creating hidden window", winPropChain)
              showNew vm Visibility.Hidden (fun () -> currentModel) dispatch
              b.VmWinState.Value <- WindowState.Hidden vm
              [ PropertyChanged name ]
          | WindowState.Closed, WindowState.Visible m ->
              let vm = newVm m
              log.LogTrace("[{BindingNameChain}] Creating visible window", winPropChain)
              showNew vm Visibility.Visible (fun () -> currentModel) dispatch
              b.VmWinState.Value <- WindowState.Visible vm
              [ PropertyChanged name ]
      | SubModelSeqUnkeyed b ->
          let d = b.SubModelSeqUnkeyedData
          let create m idx =
            let toMsg = fun msg -> d.ToMsg currentModel msg
            let chain = LoggingViewModelArgs.getNameChainForItem nameChain name (idx |> string)
            let args = ViewModelArgs.create m (fun msg -> toMsg (idx, msg) |> dispatch) chain loggingArgs
            d.CreateViewModel args
          let update vm m = d.UpdateViewModel (vm,m)
          Merge.unkeyed create update b.Vms (d.GetModels newModel)
          []
      | SubModelSeqKeyed b ->
          let d = b.SubModelSeqKeyedData
          let getTargetId getId vm = vm |> d.GetUnderlyingModel |> getId
          let create m id =
            let toMsg = fun msg -> d.ToMsg currentModel msg
            let chain = LoggingViewModelArgs.getNameChainForItem nameChain name (id |> string)
            let args = ViewModelArgs.create m (fun msg -> toMsg (id, msg) |> dispatch) chain loggingArgs
            d.CreateViewModel args
          let update vm m = d.UpdateViewModel (vm,m)
          let newSubModels = newModel |> d.GetSubModels |> Seq.toArray
          try
            d.MergeKeyed(getTargetId, create, update, b.Vms, newSubModels)
          with
            | :? Elmish.WPF.DuplicateIdException as e ->
              let messageTemplate = "[{BindingNameChain}] In the {SourceOrTarget} sequence of the binding {BindingName}, the elements at indices {Index1} and {Index2} have the same ID {ID}. To avoid this problem, the elements will be merged without using IDs."
              log.LogError(messageTemplate, nameChain, e.SourceOrTarget, name, e.Index1, e.Index2, e.Id)
              let create m _ = create m (d.GetId m)
              Merge.unkeyed create update b.Vms newSubModels
          []

  member this.Recursive<'model, 'msg>
      (currentModel: 'model,
       newModel: 'model,
       dispatch: 'msg -> unit,
       binding: VmBinding<'model, 'msg>)
      : UpdateData list =
    match binding with
      | BaseVmBinding b -> this.Base(currentModel, newModel, dispatch, b)
      | Cached b ->
          let updates = this.Recursive(currentModel, newModel, dispatch, b.Binding)
          updates
          |> List.filter UpdateData.isPropertyChanged
          |> List.iter (fun _ -> b.Cache.Value <- None)
          updates
      | Validatation b ->
          let updates = this.Recursive(currentModel, newModel, dispatch, b.Binding)
          let newErrors = b.Validate newModel
          if b.Errors.Value <> newErrors then
            b.Errors.Value <- newErrors
            ErrorsChanged name :: updates
          else
            updates
      | Lazy b ->
          let currentModel' = b.Get currentModel
          let newModel' = b.Get newModel
          if b.Equals currentModel' newModel' then
            []
          else
            this.Recursive(currentModel', newModel', b.Dispatch, b.Binding)
      | AlterMsgStream b ->
          this.Recursive(b.Get currentModel, b.Get newModel, b.Dispatch, b.Binding)


type internal Get(nameChain: string) =

  member _.Base (model: 'model, binding: BaseVmBinding<'model, 'msg>) =
    match binding with
    | OneWay { OneWayData = d } -> d.Get model |> Ok
    | TwoWay b -> b.Get model |> Ok
    | OneWayToSource _ -> GetError.OneWayToSource |> Error
    | OneWaySeq { Values = vals } -> vals.BoxedCollection() |> Ok
    | Cmd cmd -> cmd |> box |> Ok
    | SubModel { Vm = vm } -> vm.Value |> ValueOption.toObj |> box |> Ok
    | SubModelWin { VmWinState = vm } ->
        vm.Value
        |> WindowState.toVOption
        |> ValueOption.map box
        |> ValueOption.toObj
        |> Ok
    | SubModelSeqUnkeyed { Vms = vms }
    | SubModelSeqKeyed { Vms = vms } -> vms.BoxedCollection () |> Ok
    | SubModelSelectedItem b ->
        b.TryGetMember model
        |> function
          | ValueNone -> ValueNone |> Ok // deselecting successful
          | ValueSome (id, mVm) ->
              match mVm with
              | Some vm -> (id, vm) |> ValueSome |> Ok // selecting successful
              | None -> // selecting failed
                  { NameChain = nameChain
                    SubModelSeqBindingName = b.SubModelSeqBindingName
                    Id = id.ToString() }
                  |> GetError.SubModelSelectedItem
                  |> Error
        |> Result.map (ValueOption.map snd >> ValueOption.toObj >> box)


  member this.Recursive<'model, 'msg>
      (model: 'model,
       binding: VmBinding<'model, 'msg>)
      : Result<obj, GetError> =
    match binding with
    | BaseVmBinding b -> this.Base(model, b)
    | Cached b ->
        match b.Cache.Value with
        | Some v -> v |> Ok
        | None ->
            let x = this.Recursive(model, b.Binding)
            x |> Result.iter (fun v -> b.Cache.Value <- Some v)
            x
    | Validatation b -> this.Recursive(model, b.Binding)
    | Lazy b -> this.Recursive(b.Get model, b.Binding)
    | AlterMsgStream b -> this.Recursive(b.Get model, b.Binding)


type internal Set(value: obj) =

  member _.Base(model: 'model, binding: BaseVmBinding<'model, 'msg>) =
    match binding with
    | TwoWay b ->
        b.Set value model
        true
    | OneWayToSource b ->
        b.Set value model
        true
    | SubModelSelectedItem b ->
        let bindingModel = 
          value
          |> ValueOption.ofObj
          |> ValueOption.map b.SelectedItemBinding.GetUnderlyingModel
        b.TrySetMember(model, bindingModel)
        true
    | OneWay _
    | OneWaySeq _
    | Cmd _
    | SubModel _
    | SubModelWin _
    | SubModelSeqUnkeyed _
    | SubModelSeqKeyed _ ->
        false

  member this.Recursive<'model, 'msg>(model: 'model, binding: VmBinding<'model, 'msg>) : bool =
    match binding with
    | BaseVmBinding b -> this.Base(model, b)
    | Cached b ->
        let successful = this.Recursive(model, b.Binding)
        if successful then
          b.Cache.Value <- None  // TODO #185: write test
        successful
    | Validatation b -> this.Recursive(model, b.Binding)
    | Lazy b -> this.Recursive(b.Get model, b.Binding)
    | AlterMsgStream b -> this.Recursive(b.Get model, b.Binding)