package com.example.printlink

import android.content.ContentResolver
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** State observed by the Android screen. It survives configuration changes. */
data class TransferUiState(
    val status: String = "No document selected",
    val isSending: Boolean = false,
    val progress: Int = 0
)

class TransferViewModel : ViewModel() {
    private val _uiState = MutableStateFlow(TransferUiState())
    val uiState: StateFlow<TransferUiState> = _uiState.asStateFlow()

    fun beginDocumentSelection() {
        if (_uiState.value.isSending) return
        _uiState.value = TransferUiState(status = "Checking document...")
    }

    /** For validation and idle statuses, never overwrite an active transfer. */
    fun setStatus(status: String) {
        if (_uiState.value.isSending) return
        _uiState.update { it.copy(status = status) }
    }

    fun startTransfer(
        resolver: ContentResolver,
        host: String,
        uri: Uri,
        fileName: String
    ) {
        // Guard against repeated taps or a recreated UI issuing a duplicate send.
        if (_uiState.value.isSending) return

        _uiState.value = TransferUiState(
            status = "Preparing document...",
            isSending = true,
            progress = 0
        )

        // This coroutine belongs to the ViewModel, not MainActivity. It remains
        // active when rotation destroys and recreates the Compose screen.
        viewModelScope.launch {
            val result = try {
                withContext(Dispatchers.IO) {
                    PrintLinkTransferClient.send(
                        resolver = resolver,
                        host = host,
                        uri = uri,
                        fileName = fileName,
                        onProgress = { percent ->
                            _uiState.update {
                                it.copy(progress = percent.coerceIn(0, 100))
                            }
                        },
                        onStage = { stage ->
                            _uiState.update { it.copy(status = stage) }
                        }
                    )
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                val reason = e.message?.takeIf { it.isNotBlank() }
                    ?: e.javaClass.simpleName
                "Transfer failed: $reason"
            }

            _uiState.update { it.copy(status = result, isSending = false) }
        }
    }
}
