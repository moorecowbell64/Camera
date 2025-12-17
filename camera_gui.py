#!/usr/bin/env python3
"""
Jennov PTZ Camera Control - GUI Application
Beautiful, modern interface with live video streaming and recording
"""

import sys
import cv2
import numpy as np
from datetime import datetime
from PyQt5.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout,
                            QHBoxLayout, QPushButton, QLabel, QSlider, QGroupBox,
                            QGridLayout, QLineEdit, QComboBox, QMessageBox, QFrame)
from PyQt5.QtCore import Qt, QThread, pyqtSignal, QTimer
from PyQt5.QtGui import QImage, QPixmap, QFont, QPalette, QColor, QIcon
from camera_control_ultra import JennovCamera
import time

class VideoThread(QThread):
    """Thread for capturing and processing video frames - ULTRA LOW LATENCY"""
    change_pixmap_signal = pyqtSignal(np.ndarray)

    def __init__(self, rtsp_url):
        super().__init__()
        self.rtsp_url = rtsp_url
        self._run_flag = True
        self.recording = False
        self.video_writer = None
        self.recording_filename = None
        self.frame_skip_count = 0

    def run(self):
        """Capture video frames with optimized settings"""
        # Use TCP for reliability (UDP can cause freezing on some networks)
        cap = cv2.VideoCapture(self.rtsp_url, cv2.CAP_FFMPEG)

        if not cap.isOpened():
            print(f"Failed to open RTSP stream: {self.rtsp_url}")
            return

        # Low-latency settings
        cap.set(cv2.CAP_PROP_BUFFERSIZE, 3)  # Small buffer for lower latency

        print(f"Connected to RTSP stream: {self.rtsp_url}")

        consecutive_failures = 0
        max_failures = 30  # Reconnect after 30 failed reads

        while self._run_flag:
            ret, frame = cap.read()

            if ret:
                consecutive_failures = 0  # Reset failure counter

                # Emit signal with the frame
                self.change_pixmap_signal.emit(frame)

                # Record if enabled
                if self.recording and self.video_writer is not None:
                    self.video_writer.write(frame)
            else:
                consecutive_failures += 1
                print(f"Failed to read frame ({consecutive_failures}/{max_failures})")

                if consecutive_failures >= max_failures:
                    print("Too many failures, attempting reconnect...")
                    cap.release()
                    time.sleep(1)
                    cap = cv2.VideoCapture(self.rtsp_url, cv2.CAP_FFMPEG)
                    cap.set(cv2.CAP_PROP_BUFFERSIZE, 3)
                    consecutive_failures = 0
                else:
                    time.sleep(0.1)  # Wait before retry

        cap.release()
        if self.video_writer is not None:
            self.video_writer.release()

    def start_recording(self, filename):
        """Start recording video"""
        if not self.recording:
            fourcc = cv2.VideoWriter_fourcc(*'mp4v')
            self.video_writer = cv2.VideoWriter(filename, fourcc, 20.0, (1920, 1080))
            self.recording = True
            self.recording_filename = filename
            print(f"Started recording: {filename}")
            return True
        return False

    def stop_recording(self):
        """Stop recording video"""
        if self.recording:
            self.recording = False
            if self.video_writer is not None:
                self.video_writer.release()
                self.video_writer = None
            print(f"Stopped recording: {self.recording_filename}")
            return self.recording_filename
        return None

    def stop(self):
        """Stop the video thread"""
        self._run_flag = False
        if self.recording:
            self.stop_recording()
        self.wait()

class CameraGUI(QMainWindow):
    def __init__(self):
        super().__init__()
        self.camera = None
        self.video_thread = None
        self.is_connected = False

        # PTZ state
        self.ptz_speed = 0.5
        self.zoom_speed = 0.3

        # Camera configuration
        self.camera_ip = "192.168.50.224"
        self.camera_user = "admin"
        self.camera_pass = "hydroLob99"
        self.rtsp_url = f"rtsp://{self.camera_user}:{self.camera_pass}@{self.camera_ip}:554/stream1"

        self.initUI()

    def initUI(self):
        """Initialize the user interface"""
        self.setWindowTitle("Jennov PTZ Camera Control")
        self.setGeometry(100, 100, 1400, 900)

        # Set modern dark theme
        self.setStyleSheet("""
            QMainWindow {
                background-color: #2b2b2b;
            }
            QGroupBox {
                border: 2px solid #3daee9;
                border-radius: 8px;
                margin-top: 12px;
                padding-top: 15px;
                font-weight: bold;
                color: #ffffff;
                background-color: #1e1e1e;
            }
            QGroupBox::title {
                subcontrol-origin: margin;
                left: 15px;
                padding: 0 5px;
                color: #3daee9;
            }
            QPushButton {
                background-color: #3daee9;
                color: white;
                border: none;
                padding: 10px 20px;
                border-radius: 5px;
                font-weight: bold;
                font-size: 12px;
            }
            QPushButton:hover {
                background-color: #4dbef5;
            }
            QPushButton:pressed {
                background-color: #2d8ec4;
            }
            QPushButton:disabled {
                background-color: #555555;
                color: #888888;
            }
            QLabel {
                color: #ffffff;
                font-size: 12px;
            }
            QSlider::groove:horizontal {
                border: 1px solid #999999;
                height: 8px;
                background: #1e1e1e;
                margin: 2px 0;
                border-radius: 4px;
            }
            QSlider::handle:horizontal {
                background: #3daee9;
                border: 1px solid #3daee9;
                width: 18px;
                margin: -5px 0;
                border-radius: 9px;
            }
            QLineEdit, QComboBox {
                background-color: #1e1e1e;
                color: white;
                border: 1px solid #3daee9;
                border-radius: 4px;
                padding: 5px;
            }
            .recording {
                background-color: #da4453;
            }
            .recording:hover {
                background-color: #e55463;
            }
        """)

        # Main widget and layout
        main_widget = QWidget()
        self.setCentralWidget(main_widget)
        main_layout = QHBoxLayout()
        main_widget.setLayout(main_layout)

        # Left panel - Video display
        left_panel = QVBoxLayout()

        # Video display
        self.video_label = QLabel()
        self.video_label.setMinimumSize(960, 540)
        self.video_label.setMaximumSize(1280, 720)
        self.video_label.setStyleSheet("border: 2px solid #3daee9; background-color: #000000;")
        self.video_label.setAlignment(Qt.AlignCenter)
        self.video_label.setText("No Video Feed\n\nClick 'Connect' to start")
        font = QFont("Arial", 16)
        self.video_label.setFont(font)
        left_panel.addWidget(self.video_label)

        # Status bar
        status_layout = QHBoxLayout()
        self.status_label = QLabel("● Disconnected")
        self.status_label.setStyleSheet("color: #da4453; font-size: 14px; font-weight: bold;")
        status_layout.addWidget(self.status_label)
        status_layout.addStretch()

        self.recording_label = QLabel("⏺ Not Recording")
        self.recording_label.setStyleSheet("color: #888888; font-size: 14px;")
        status_layout.addWidget(self.recording_label)

        left_panel.addLayout(status_layout)

        main_layout.addLayout(left_panel, 70)

        # Right panel - Controls
        right_panel = QVBoxLayout()

        # Connection controls
        conn_group = QGroupBox("Connection")
        conn_layout = QVBoxLayout()

        self.connect_btn = QPushButton("🔌 Connect Camera")
        self.connect_btn.clicked.connect(self.toggle_connection)
        self.connect_btn.setMinimumHeight(40)
        conn_layout.addWidget(self.connect_btn)

        conn_group.setLayout(conn_layout)
        right_panel.addWidget(conn_group)

        # PTZ Controls
        ptz_group = QGroupBox("PTZ Controls")
        ptz_layout = QVBoxLayout()

        # Speed control
        speed_layout = QHBoxLayout()
        speed_layout.addWidget(QLabel("Speed:"))
        self.speed_slider = QSlider(Qt.Horizontal)
        self.speed_slider.setMinimum(1)
        self.speed_slider.setMaximum(10)
        self.speed_slider.setValue(5)
        self.speed_slider.valueChanged.connect(self.update_speed)
        speed_layout.addWidget(self.speed_slider)
        self.speed_value = QLabel("0.5")
        self.speed_value.setStyleSheet("color: #3daee9; font-weight: bold;")
        speed_layout.addWidget(self.speed_value)
        ptz_layout.addLayout(speed_layout)

        # Directional controls (circular pad)
        dir_grid = QGridLayout()
        dir_grid.setSpacing(5)

        # Up
        self.btn_up = QPushButton("▲")
        self.btn_up.setMinimumSize(60, 60)
        self.btn_up.pressed.connect(lambda: self.start_move("up"))
        self.btn_up.released.connect(self.stop_move)
        dir_grid.addWidget(self.btn_up, 0, 1)

        # Left
        self.btn_left = QPushButton("◄")
        self.btn_left.setMinimumSize(60, 60)
        self.btn_left.pressed.connect(lambda: self.start_move("left"))
        self.btn_left.released.connect(self.stop_move)
        dir_grid.addWidget(self.btn_left, 1, 0)

        # Center (stop)
        self.btn_stop = QPushButton("⏹")
        self.btn_stop.setMinimumSize(60, 60)
        self.btn_stop.clicked.connect(self.stop_move)
        self.btn_stop.setStyleSheet("QPushButton { background-color: #da4453; }")
        dir_grid.addWidget(self.btn_stop, 1, 1)

        # Right
        self.btn_right = QPushButton("►")
        self.btn_right.setMinimumSize(60, 60)
        self.btn_right.pressed.connect(lambda: self.start_move("right"))
        self.btn_right.released.connect(self.stop_move)
        dir_grid.addWidget(self.btn_right, 1, 2)

        # Down
        self.btn_down = QPushButton("▼")
        self.btn_down.setMinimumSize(60, 60)
        self.btn_down.pressed.connect(lambda: self.start_move("down"))
        self.btn_down.released.connect(self.stop_move)
        dir_grid.addWidget(self.btn_down, 2, 1)

        ptz_layout.addLayout(dir_grid)

        # Zoom controls
        zoom_layout = QHBoxLayout()
        self.btn_zoom_in = QPushButton("🔍+ Zoom In")
        self.btn_zoom_in.pressed.connect(lambda: self.start_move("zoom_in"))
        self.btn_zoom_in.released.connect(self.stop_move)
        zoom_layout.addWidget(self.btn_zoom_in)

        self.btn_zoom_out = QPushButton("🔍- Zoom Out")
        self.btn_zoom_out.pressed.connect(lambda: self.start_move("zoom_out"))
        self.btn_zoom_out.released.connect(self.stop_move)
        zoom_layout.addWidget(self.btn_zoom_out)
        ptz_layout.addLayout(zoom_layout)

        ptz_group.setLayout(ptz_layout)
        right_panel.addWidget(ptz_group)

        # Preset controls
        preset_group = QGroupBox("Presets")
        preset_layout = QVBoxLayout()

        preset_select_layout = QHBoxLayout()
        preset_select_layout.addWidget(QLabel("Preset:"))
        self.preset_combo = QComboBox()
        self.preset_combo.addItems(["1", "2", "3", "4", "5", "6", "7", "8"])
        preset_select_layout.addWidget(self.preset_combo)
        preset_layout.addLayout(preset_select_layout)

        self.btn_goto_preset = QPushButton("📍 Go to Preset")
        self.btn_goto_preset.clicked.connect(self.goto_preset)
        preset_layout.addWidget(self.btn_goto_preset)

        preset_group.setLayout(preset_layout)
        right_panel.addWidget(preset_group)

        # Recording controls
        rec_group = QGroupBox("Recording")
        rec_layout = QVBoxLayout()

        self.btn_record = QPushButton("⏺ Start Recording")
        self.btn_record.clicked.connect(self.toggle_recording)
        self.btn_record.setMinimumHeight(40)
        rec_layout.addWidget(self.btn_record)

        self.btn_snapshot = QPushButton("📷 Take Snapshot")
        self.btn_snapshot.clicked.connect(self.take_snapshot)
        rec_layout.addWidget(self.btn_snapshot)

        rec_group.setLayout(rec_layout)
        right_panel.addWidget(rec_group)

        right_panel.addStretch()

        main_layout.addLayout(right_panel, 30)

        # Disable PTZ controls initially
        self.set_controls_enabled(False)

    def toggle_connection(self):
        """Connect or disconnect from camera"""
        if not self.is_connected:
            try:
                # Connect to camera
                self.status_label.setText("● Connecting...")
                self.status_label.setStyleSheet("color: #f67400; font-size: 14px; font-weight: bold;")
                QApplication.processEvents()

                self.camera = JennovCamera(self.camera_ip, self.camera_user, self.camera_pass)

                # Start video thread
                self.video_thread = VideoThread(self.rtsp_url)
                self.video_thread.change_pixmap_signal.connect(self.update_image)
                self.video_thread.start()

                self.is_connected = True
                self.connect_btn.setText("🔌 Disconnect")
                self.status_label.setText("● Connected")
                self.status_label.setStyleSheet("color: #27ae60; font-size: 14px; font-weight: bold;")
                self.set_controls_enabled(True)

            except Exception as e:
                QMessageBox.critical(self, "Connection Error", f"Failed to connect to camera:\n{str(e)}")
                self.status_label.setText("● Connection Failed")
                self.status_label.setStyleSheet("color: #da4453; font-size: 14px; font-weight: bold;")
        else:
            # Disconnect
            if self.video_thread is not None:
                self.video_thread.stop()
                self.video_thread = None

            self.camera = None
            self.is_connected = False
            self.connect_btn.setText("🔌 Connect Camera")
            self.status_label.setText("● Disconnected")
            self.status_label.setStyleSheet("color: #da4453; font-size: 14px; font-weight: bold;")
            self.video_label.setText("No Video Feed\n\nClick 'Connect' to start")
            self.set_controls_enabled(False)

    def update_image(self, cv_img):
        """Update the video display with new frame"""
        # Convert to RGB
        rgb_image = cv2.cvtColor(cv_img, cv2.COLOR_BGR2RGB)
        h, w, ch = rgb_image.shape
        bytes_per_line = ch * w

        # Convert to QImage
        qt_image = QImage(rgb_image.data, w, h, bytes_per_line, QImage.Format_RGB888)

        # Scale to fit label (use FastTransformation for better performance)
        scaled_pixmap = QPixmap.fromImage(qt_image).scaled(
            self.video_label.width(),
            self.video_label.height(),
            Qt.KeepAspectRatio,
            Qt.FastTransformation
        )

        self.video_label.setPixmap(scaled_pixmap)

    def set_controls_enabled(self, enabled):
        """Enable or disable PTZ controls"""
        self.btn_up.setEnabled(enabled)
        self.btn_down.setEnabled(enabled)
        self.btn_left.setEnabled(enabled)
        self.btn_right.setEnabled(enabled)
        self.btn_stop.setEnabled(enabled)
        self.btn_zoom_in.setEnabled(enabled)
        self.btn_zoom_out.setEnabled(enabled)
        self.btn_goto_preset.setEnabled(enabled)
        self.btn_record.setEnabled(enabled)
        self.btn_snapshot.setEnabled(enabled)
        self.speed_slider.setEnabled(enabled)

    def update_speed(self, value):
        """Update PTZ speed from slider"""
        self.ptz_speed = value / 10.0
        self.speed_value.setText(f"{self.ptz_speed:.1f}")

    def start_move(self, direction):
        """Start camera movement - ULTRA INSTANT response"""
        if not self.camera:
            return

        try:
            # Use move_instant() for absolute minimum latency
            speed = self.zoom_speed if 'zoom' in direction else self.ptz_speed
            self.camera.move_instant(direction, speed)
        except Exception as e:
            print(f"Move error: {e}")

    def stop_move(self):
        """Stop camera movement - INSTANT"""
        if self.camera:
            try:
                self.camera.stop_instant()
            except Exception as e:
                print(f"Stop error: {e}")

    def goto_preset(self):
        """Go to selected preset position"""
        if not self.camera:
            return

        preset = self.preset_combo.currentText()
        try:
            self.camera.goto_preset(preset, 1.0)
            self.status_label.setText(f"● Moving to Preset {preset}")
            QTimer.singleShot(2000, lambda: self.status_label.setText("● Connected"))
        except Exception as e:
            QMessageBox.warning(self, "Preset Error", f"Failed to go to preset:\n{str(e)}")

    def toggle_recording(self):
        """Start or stop video recording"""
        if not self.video_thread:
            return

        if not self.video_thread.recording:
            # Start recording
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            filename = f"recording_{timestamp}.mp4"

            if self.video_thread.start_recording(filename):
                self.btn_record.setText("⏹ Stop Recording")
                self.btn_record.setProperty("class", "recording")
                self.btn_record.setStyleSheet(self.styleSheet())  # Refresh style
                self.recording_label.setText(f"⏺ Recording: {filename}")
                self.recording_label.setStyleSheet("color: #da4453; font-size: 14px; font-weight: bold;")
        else:
            # Stop recording
            filename = self.video_thread.stop_recording()
            self.btn_record.setText("⏺ Start Recording")
            self.btn_record.setProperty("class", "")
            self.btn_record.setStyleSheet(self.styleSheet())  # Refresh style
            self.recording_label.setText("⏺ Not Recording")
            self.recording_label.setStyleSheet("color: #888888; font-size: 14px;")

            if filename:
                QMessageBox.information(self, "Recording Saved", f"Recording saved to:\n{filename}")

    def take_snapshot(self):
        """Take a snapshot from video feed"""
        if not self.video_thread:
            return

        # Capture current frame from video label
        pixmap = self.video_label.pixmap()
        if pixmap:
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            filename = f"snapshot_{timestamp}.jpg"
            pixmap.save(filename, "JPG")
            QMessageBox.information(self, "Snapshot Saved", f"Snapshot saved to:\n{filename}")

    def closeEvent(self, event):
        """Clean up on window close"""
        if self.video_thread is not None:
            self.video_thread.stop()
        event.accept()

def main():
    app = QApplication(sys.argv)

    # Set application icon (optional)
    app.setApplicationName("Jennov PTZ Camera Control")

    gui = CameraGUI()
    gui.show()

    sys.exit(app.exec_())

if __name__ == '__main__':
    main()
