#!/bin/bash

# 设置变量
IMAGE_NAME="rixapi/mjopen"
VERSION=$(date +%Y%m%d%H%M) # 使用时间戳作为版本号
LATEST_TAG="latest"

# 显示构建信息
echo "开始构建 Docker 镜像: $IMAGE_NAME:$VERSION"

# 构建Docker镜像
docker build --platform linux/amd64 -t $IMAGE_NAME:$VERSION -t $IMAGE_NAME:$LATEST_TAG -f src/Midjourney.API/Dockerfile .

# 检查构建是否成功
if [ $? -eq 0 ]; then
    echo "镜像构建成功: $IMAGE_NAME:$VERSION"
    
    # 询问是否推送到Docker Hub
    read -p "是否推送镜像到Docker Hub? (y/n): " PUSH_CHOICE
    
    if [ "$PUSH_CHOICE" = "y" ] || [ "$PUSH_CHOICE" = "Y" ]; then
        echo "推送镜像到Docker Hub..."
        
        # 推送指定版本
        docker push $IMAGE_NAME:$VERSION
        
        # 推送latest标签
        docker push $IMAGE_NAME:$LATEST_TAG
        
        echo "镜像推送完成"
    else
        echo "已跳过推送镜像操作"
        echo "如需手动推送，请使用以下命令:"
        echo "docker push $IMAGE_NAME:$VERSION"
        echo "docker push $IMAGE_NAME:$LATEST_TAG"
    fi
else
    echo "镜像构建失败"
    exit 1
fi

echo "完成" 